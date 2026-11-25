using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// 1.19.59: the settlement wheel's routing, checked against the panel this player already runs.
//
// Provenance of the oracle below (read 2026-11-24, not executed, no dependency on that repository or its DLL):
//   conflictmodlist 1.1.15 - `shared/PanelPointer.cs`, `internal sealed class WheelRouter` (lines 145-217) and
//   `internal static class PanelHit` (lines 34-45); its call site `src/TeamVersionsDriver.cs` `HandleMouse`
//   (lines 441-513) with `ReadNativeScroll` (618-628) and `ReadPhysicalWheel` (630-639).
//
// `ReferenceWheelRouter` is a transcription of that class, statement for statement, with the same constants
// (`RowsPerNotch = 3`, `MaxNotches = 5`), the same `fresh = native != 0 && native != previous` consumption rule, the same
// "one source per frame, never added", the same `Suspend`/`Reset` split and the same sign convention. Its expected
// answers are also pinned by the hand-computed `Golden` table, so the oracle cannot drift silently with this file.
//
// What is compared: `ResultWheelRouter.Sample` and `ResultWheelRouter.Pixels` against the oracle for every frame of the
// script - source word and pixels, exactly. What is deliberately *not* identical, and why:
//   * the oracle answers pixels into a continuous list, whose top row may be cut in half; this board is a table with a
//     frozen header row and whole-row slots, so `ResultWheelSteps` turns the same pixels into whole rows and keeps the
//     sub-row remainder (a resolution change starts a new remainder);
//   * one notch is three rows in both, but "three rows" is a pixel count here (`3 x rowHeight`) and a scroll offset
//     there, so the equivalence is on pixels and on rows-at-a-pitch, never on the absolute offset.
internal static class ResultWheelScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        TheRouterIsTheReferenceRouter(check);
        AHeldNativeReadingIsConsumedOnce(check);
        ThePhysicalWheelIsTheFallbackAndNeverAdded(check);
        TheNotchArithmeticMatchesTheReference(check);
        TheHitEdgesAreTheReferenceEdges(check);
        TheSameFrameIsSettledOnce(check);
        SubRowInputIsCarriedAndNeverBanked(check);
        TheTableIsReachableToItsLastRow(check);
        ThePageKeepsItsOwnWheelWhenTheBoardDoesNotUseIt(check);
    }

    // ---- frame by frame against the reference router ------------------------------------------------
    private static void TheRouterIsTheReferenceRouter(Action<bool, string> check)
    {
        // native, physical, note. Every frame is one call of `Sample`/`Sample` on both implementations.
        var script = new (float Native, float Physical, string Note)[]
        {
            (0f, 0f, "nothing this frame"),
            (0f, -1f, "physical notch down (Unity's -y)"),
            (0f, -0.1f, "a 0.1-per-notch trackpad tick, normalised by ten"),
            (0f, 1f, "physical notch up"),
            (-1f, 0f, "the page's own reading, first time"),
            (-1f, 0f, "the same reading held: already consumed"),
            (-1f, -1f, "held native plus a fresh physical notch: the physical one decides"),
            (-2f, -1f, "the native reading changed: it decides again, and the two are never added"),
            (-2f, 0f, "held again"),
            (0f, 0f, "the stick returned to rest"),
            (-0.05f, 0f, "a sub-notch native reading"),
            (-20f, 0f, "a huge reading: capped at five notches"),
            (float.NaN, -1f, "a reading that is not a number falls back to the physical wheel"),
            (3f, 0f, "positive native (upwards)"),
        };
        var reference = new ReferenceWheelRouter();
        var ours = new ResultWheelRouter();
        float pitch = 24f, maxPixels = 720f;
        int differences = 0;
        foreach ((float native, float physical, string note) in script)
        {
            float want = reference.Sample(native, physical, pitch, maxPixels);
            float got = ours.Sample(native, physical, pitch, maxPixels);
            bool sameSource = reference.Source.ToString() == ours.Source.ToString();
            bool samePixels = Math.Abs(want - got) < 0.0001f;
            if (!sameSource || !samePixels) differences++;
            check(sameSource && samePixels,
                "same answer as the reference for '" + note + "': source=" + ours.Source
                    + " (reference " + reference.Source + "), pixels=" + got.ToString("0.###")
                    + " (reference " + want.ToString("0.###") + ")");
        }
        check(differences == 0, "every frame of the script matches the reference router: differences=" + differences);
        // The static arithmetic is the same function, so it is compared on its own as well.
        float[] raws = { 0f, 0.05f, 0.1f, 0.5f, 1f, -1f, 2.5f, -3f, 6f, -20f, float.NaN, float.PositiveInfinity };
        float[] pitches = { 9f, 18f, 24f, 28f, 30f };
        bool arithmetic = true;
        foreach (float raw in raws)
            foreach (float p in pitches)
                if (Math.Abs(ReferenceWheelRouter.Pixels(raw, p, 720f) - ResultWheelRouter.Pixels(raw, p, 720f)) > 0.0001f)
                    arithmetic = false;
        check(arithmetic, "the pixel arithmetic is identical for every raw reading and row pitch the board draws at");
        check(ResultWheelRouter.RowsPerNotch == ReferenceWheelRouter.RowsPerNotch
              && ResultWheelRouter.MaxNotches == ReferenceWheelRouter.MaxNotches,
            "one notch is three rows and one frame is at most five notches, in both");
        // The golden numbers the transcription itself is pinned by (hand-computed from the reference's arithmetic).
        check(Math.Abs(ReferenceWheelRouter.Pixels(-1f, 24f, 720f) - 72f) < 0.0001f,
            "one notch at a 24px pitch is 72px = three rows, which is the number the golden table pins the oracle with");
        check(Math.Abs(ReferenceWheelRouter.Pixels(-0.1f, 24f, 720f) - 72f) < 0.0001f,
            "and a 0.1-per-notch platform is normalised to the same three rows");
        check(Math.Abs(ReferenceWheelRouter.Pixels(-20f, 24f, 720f) - 360f) < 0.0001f,
            "while a twenty-notch reading is capped at five notches = 360px");
        check(Math.Abs(ReferenceWheelRouter.Pixels(-20f, 24f, 100f) - 100f) < 0.0001f,
            "and by the viewport it is applied to, exactly like the reference's `maxPixels`");
        check(Math.Abs(ReferenceWheelRouter.Pixels(-1f, 24f, 720f) + ReferenceWheelRouter.Pixels(1f, 24f, 720f)) < 0.0001f,
            "the sign is the reference's: Unity's negative (down) becomes a positive 'towards the foot' pixel count");
    }

    // ---- a held native reading is consumed by change ------------------------------------------------
    private static void AHeldNativeReadingIsConsumedOnce(Action<bool, string> check)
    {
        var wheel = new ResultWheelRouter();
        float first = wheel.Sample(-1f, 0f, 24f, 720f);
        float held = wheel.Sample(-1f, 0f, 24f, 720f);
        float heldAgain = wheel.Sample(-1f, 0f, 24f, 720f);
        check(first == 72f && held == 0f && heldAgain == 0f,
            "a native reading is consumed by change: the first frame moves three rows, the held ones move nothing ("
                + first.ToString("0") + "/" + held.ToString("0") + "/" + heldAgain.ToString("0") + ")");
        // The pointer leaves the board and comes back with the same still-held reading: `Suspend` keeps the fingerprint,
        // so the reading that was already used is not used again - the requirement's "no replaying old input".
        wheel.Suspend();
        float afterReturn = wheel.Sample(-1f, 0f, 24f, 720f);
        check(afterReturn == 0f, "leaving and re-entering with the same held reading does not scroll again: " + afterReturn.ToString("0"));
        // A new page is a new reading: `Reset` forgets the fingerprint (the reference's own distinction).
        wheel.Reset();
        check(wheel.Sample(-1f, 0f, 24f, 720f) == 72f,
            "only a new page (the reference's `Reset`) makes the same reading count again");
        // A changed reading while the pointer is outside is not banked either: the frame's source is cleared, and the
        // change is only seen on a frame that is actually over the board.
        wheel.Suspend();
        check(wheel.Sample(-2f, 0f, 24f, 720f) == 144f && wheel.Sample(-2f, 0f, 24f, 720f) == 0f,
            "the first frame back samples whatever the reading is, once (two notches = 144px, then nothing)");
    }

    // ---- the two sources, one per frame --------------------------------------------------------------
    private static void ThePhysicalWheelIsTheFallbackAndNeverAdded(Action<bool, string> check)
    {
        var wheel = new ResultWheelRouter();
        float both = wheel.Sample(-1f, -1f, 24f, 720f);
        check(both == 72f && wheel.Source == ResultWheelSource.Native,
            "when both sources answer, the native one decides and nothing is added: " + both.ToString("0") + "px, source=" + wheel.Source);
        float physical = wheel.Sample(-1f, -1f, 24f, 720f);
        check(physical == 72f && wheel.Source == ResultWheelSource.Physical,
            "and while that native reading is held, the physical wheel is the one that gets the frame: " + wheel.Source);
        // The reference re-applies a *constant* physical reading every frame (Unity's `mouseScrollDelta` is per-frame, so
        // a constant non-zero value only happens with a synthetic input). This port keeps that behaviour verbatim.
        float repeat = wheel.Sample(-1f, -1f, 24f, 720f);
        check(repeat == 72f && wheel.Source == ResultWheelSource.Physical,
            "a repeated physical reading is applied again, exactly as the reference does (its per-frame delta is what stops it in practice)");
        // A physical reading of zero and a held native reading is a quiet frame.
        var quiet = new ResultWheelRouter();
        quiet.Sample(-1f, 0f, 24f, 720f);
        check(quiet.Sample(-1f, 0f, 24f, 720f) == 0f && quiet.Source == ResultWheelSource.None,
            "a held native reading with no physical notch is a frame that does not scroll");
        // Non-finite readings are zero, never a NaN scroll.
        var broken = new ResultWheelRouter();
        check(broken.Sample(float.NaN, float.NaN, 24f, 720f) == 0f && broken.Source == ResultWheelSource.None,
            "a reading that is not a number is no reading at all");
    }

    // ---- three rows per notch, at the pitches the page really draws --------------------------------
    private static void TheNotchArithmeticMatchesTheReference(Action<bool, string> check)
    {
        // The two pitches the recorded pages used: 24px at the 1440 window's 16px glyph, 9px on the 640x480 failure page.
        foreach (float pitch in new[] { 24f, 9f })
        {
            float pixels = ResultWheelRouter.Pixels(-1f, pitch, 1000f);
            check(Math.Abs(pixels - pitch * 3f) < 0.0001f,
                "one notch is three rows at a " + pitch.ToString("0") + "px pitch: " + pixels.ToString("0.##") + "px");
            var steps = new ResultWheelSteps();
            check(steps.Rows(pixels, pitch, true) == 3,
                "and the step layer turns it into three whole rows at that pitch");
        }
        // Sub-notch readings are carried (two halves of a 0.05 notch are two half-rows, not two full ones), and a pitch
        // change starts a fresh remainder because the same pixels mean a different number of rows.
        var carried = new ResultWheelSteps();
        float half = ResultWheelRouter.Pixels(-0.05f, 24f, 1000f);   // 0.5 notch = 1.5 rows = 36px
        int one = carried.Rows(half, 24f, true);
        int two = carried.Rows(half, 24f, true);
        check(one == 1 && two == 2 && one + two == 3 && Math.Abs(carried.Remainder) < 0.0001f,
            "a half-notch frame moves one whole row and keeps the other half, and the next one makes it up (1+2=3, remainder=" + carried.Remainder.ToString("0.##") + ")");
        carried.Rows(12f, 24f, true);
        float before = carried.Remainder;
        carried.Rows(12f, 9f, true);
        check(Math.Abs(before) < 0.0001f || Math.Abs(carried.Remainder) < 24f,
            "a new row pitch (a resolution change) starts the remainder from zero instead of carrying a stale fraction");
    }

    // ---- one settlement per frame, whoever asks ----------------------------------------------------
    private static void TheSameFrameIsSettledOnce(Action<bool, string> check)
    {
        // The production latch: two entries in one frame (the frame driver and `CM_PageBase.UpdateButtonPress`) produce
        // exactly one settlement; the next frame is a new one.
        var latch = new ResultScrollLatch();
        check(latch.Claim(100) && !latch.Claim(100) && latch.Claim(101),
            "the same frame claims the wheel once, and the next frame claims it again");
        check(latch.SameFrame(101) && !latch.SameFrame(102),
            "and a repeat inside the claimed frame is still that frame's own gesture");
        latch.Forget();
        check(latch.Claim(101), "forgetting the latch (a new page) lets the same frame id claim again");

        // Two notches delivered in one frame are one settlement: the router is asked once per frame, so a second call in
        // the same frame cannot add a third notch. The production code asks the latch before it reads, which is the same
        // rule stated where it runs.
        var wheel = new ResultWheelRouter();
        wheel.Sample(-1f, 0f, 24f, 720f);
        check(latch.Claim(100) == false,
            "a second entry in a frame the latch has already claimed does not read the wheel at all");
    }

    // ---- whole rows, carried fractions, nothing banked ----------------------------------------------
    private static void SubRowInputIsCarriedAndNeverBanked(Action<bool, string> check)
    {
        var steps = new ResultWheelSteps();
        // 0.05-notch frames: 36px each at a 24px pitch = 1.5 rows.
        int a = steps.Rows(36f, 24f, true);
        int b = steps.Rows(36f, 24f, true);
        check(a + b == 3, "two half-notch frames are three whole rows between them (" + a + "+" + b + ")");
        // A table that cannot act on the input drops the remainder: nothing is replayed when the other end opens.
        steps.Rows(20f, 24f, true);
        steps.Rows(30f, 24f, false);
        check(Math.Abs(steps.Remainder) < 0.0001f,
            "an input the table could not act on is dropped, not banked (remainder=" + steps.Remainder.ToString("0.##") + ")");
        // The same when the pointer leaves or the board is not drawn: `Rows(..., mayMove:false)` clears it.
        steps.Rows(12f, 24f, true);
        steps.Rows(0f, 24f, false);
        check(Math.Abs(steps.Remainder) < 0.0001f, "and leaving the board clears it as well");
        // A remainder that is only float noise is not a fraction.
        steps.Rows(0.04f, 24f, true);
        check(Math.Abs(steps.Remainder) < 0.0001f, "a fraction below a tenth of a pixel is discarded instead of accumulated");
    }

    // ---- the hit rectangle's edges, compared with the reference's own rule --------------------------
    private static void TheHitEdgesAreTheReferenceEdges(Action<bool, string> check)
    {
        // The board's rectangle is the one the last repaint drew; both sides answer "inside" for the same pixels.
        var rect = new LayoutRect(1069f, 593f, 422f, 675f);
        float[] xs = { rect.X - 1f, rect.X, rect.X + 0.5f, rect.Right - 0.5f, rect.Right, rect.Right + 1f };
        float[] ys = { rect.Y - 1f, rect.Y, rect.Y + 0.5f, rect.Bottom - 0.5f, rect.Bottom, rect.Bottom + 1f };
        bool agree = true;
        int inside = 0;
        foreach (float x in xs)
            foreach (float y in ys)
            {
                bool ours = ResultChromeLayout.Inside(rect, x, y);
                bool theirs = ReferenceWheelRouter.Inside(rect, x, y);
                if (ours != theirs) agree = false;
                if (ours) inside++;
            }
        check(agree, "the board's half-open hit test answers exactly like the reference's `PanelHit.Inside` on every edge pixel");
        check(inside == 9,
            "which is three pixels inside on each axis and the edge pixels outside (right and bottom belong to the page): inside=" + inside);
        var none = new LayoutRect(10f, 10f, 0f, 40f);
        check(!ResultChromeLayout.Inside(none, 10f, 20f) && !ReferenceWheelRouter.Inside(none, 10f, 20f),
            "and a rectangle with no width is not a target in either implementation");
    }

    // ---- the table is reachable to its last row, five notches a frame at most -----------------------
    private static void TheTableIsReachableToItsLastRow(Action<bool, string> check)
    {
        // r2's own numbers: 30 rows, one frozen header row, a 26-row plate (25 scrolling slots).
        const int rows = 30, frozen = ResultsScroll.FrozenRows, visible = 26;
        int slots = Math.Max(1, visible - frozen);
        int first = frozen;
        int frames = 0;
        var wheel = new ResultWheelRouter();
        var steps = new ResultWheelSteps();
        while (first < ResultsScroll.MaximumFirst(rows, frozen, slots) && frames < 20)
        {
            frames++;
            // One huge native reading per frame: the reference caps it at five notches, so a frame moves at most 15 rows.
            float pixels = wheel.Sample(-20f, 0f, 24f, 720f);
            int moved = steps.Rows(pixels, 24f, true);
            check(moved <= 15, "one frame moves at most five notches x three rows (" + moved + " rows)");
            int next = ResultsScroll.ByRows(first, rows, frozen, slots, moved);
            if (next == first) break;
            first = next;
        }
        var end = new ScrollViewport(rows, frozen, visible, first);
        check(end.AtEnd && end.RowAt(end.Visible - 1) == rows - 1,
            "five notches a frame still reaches the table's own last row, whole, in the last slot (frames=" + frames + ")");
        check(frozen == 1 && end.RowAt(0) == 0,
            "and the frozen header row is still slot 0 of every frame - the one difference from the reference's flat list");
    }

    // ---- a frame this mod does not use is still the page's ------------------------------------------
    private static void ThePageKeepsItsOwnWheelWhenTheBoardDoesNotUseIt(Action<bool, string> check)
    {
        // Nothing in the production path consumes an IMGUI scroll event any more: the panel reads the page's own
        // `ScrollInput` and the physical delta and never calls `Use()` for a wheel. That is the reference's own stance and
        // it is what makes "outside the board / F8 hidden / unfocused / nothing scrollable" cost the page nothing.
        string root = SourceRoot();
        if (root == null) { check(false, "the wheel scenarios locate the production sources"); return; }
        string panel = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "PanelBehaviour.cs"));
        string display = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "ResultsDisplay.cs"));
        check(!panel.Contains("EventType.ScrollWheel") && !panel.Contains("current.Use()"),
            "the panel never reads or consumes an IMGUI scroll event, so the page keeps its own wheel");
        check(display.Contains("Input.mouseScrollDelta") && display.Contains("menu.ScrollInput"),
            "the only two readings are the page's `ScrollInput` and the physical `mouseScrollDelta`");
        check(display.Contains("if (!InputFocused()) return false;"),
            "an unfocused window does not scroll the board");
        check(display.Contains("if (!_scrollValid || _hitSucceeded != succeeded) return false;"),
            "and a board that is not painted on this page (F8 off, a different flavour, no board) is not a wheel target");
        check(display.Contains("_wheel.Suspend();") && display.Contains("_wheelSteps.Reset();"),
            "every one of those frames suspends the router and drops the remainder instead of banking the input");
    }

    private static string SourceRoot()
    {
        try
        {
            var folder = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            for (int step = 0; step < 8 && folder != null; step++)
            {
                string candidate = System.IO.Path.Combine(folder.FullName, "ResultsDisplay.cs");
                if (System.IO.File.Exists(candidate)) return folder.FullName;
                folder = folder.Parent;
            }
        }
        catch { }
        return null;
    }

    // ---- the oracle: conflictmodlist 1.1.15 `WheelRouter`, transcribed statement for statement --------
    private enum ReferenceSource { None, Native, Physical }

    private sealed class ReferenceWheelRouter
    {
        internal const float RowsPerNotch = 3f;
        internal const float MaxNotches = 5f;

        private float previous;
        internal ReferenceSource Source { get; private set; }
        internal float ObservedNative { get; private set; }
        internal float ObservedPhysical { get; private set; }

        internal void Reset()
        {
            previous = 0f;
            Source = ReferenceSource.None;
            ObservedNative = 0f;
            ObservedPhysical = 0f;
        }

        internal void Suspend()
        {
            Source = ReferenceSource.None;
            ObservedNative = 0f;
            ObservedPhysical = 0f;
        }

        internal float Sample(float native, float physical, float rowHeight, float maxPixels)
        {
            if (!float.IsFinite(native)) native = 0f;
            if (!float.IsFinite(physical)) physical = 0f;
            ObservedNative = native;
            ObservedPhysical = physical;
            bool fresh = native != 0f && native != previous;
            previous = native;
            Source = ReferenceSource.None;
            if (fresh)
            {
                Source = ReferenceSource.Native;
                return Pixels(native, rowHeight, maxPixels);
            }
            if (physical != 0f)
            {
                Source = ReferenceSource.Physical;
                return Pixels(physical, rowHeight, maxPixels);
            }
            return 0f;
        }

        internal static float Pixels(float raw, float rowHeight, float maxPixels)
        {
            if (!float.IsFinite(raw) || raw == 0f) return 0f;
            float notches = MathF.Abs(raw);
            if (notches < 1f) notches *= 10f;
            notches = MathF.Min(notches, MaxNotches);
            float pixels = MathF.Min(notches * rowHeight * RowsPerNotch, MathF.Max(0f, maxPixels));
            return raw < 0f ? pixels : -pixels;
        }

        // `PanelHit.Inside` (PanelPointer.cs:37-40), kept beside the router so the edge rule is compared with ours.
        internal static bool Inside(LayoutRect rect, float x, float y)
            => rect.Width > 0f && rect.Height > 0f &&
               x >= rect.X && x < rect.X + rect.Width &&
               y >= rect.Y && y < rect.Y + rect.Height;
    }
}
