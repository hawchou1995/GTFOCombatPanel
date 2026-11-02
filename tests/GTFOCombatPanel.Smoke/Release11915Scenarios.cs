using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// What 1.19.15 changes, driven through the real policy it ships with.
//
// The defect: a guest's settlement page always carries the "waiting for the host" status line, and that line is
// itself an active control. 1.19.13/1.19.14 made the board conditional on a complete interaction scan proving the
// page holds no active control at all, so that proof could never be reached on a guest page, `ResultNoFooterGate`
// never answered Ready, and `ResultsDisplay.Placement` quietly refused to place the board for ever. Pressing F8
// clears the placement and not that gate, so F8 could not bring the board back either.
//
// What replaced it: both guest pages wait only for their own anchor to finish drawing (the failure page's
// `m_missionFailed_text` at a real alpha of 0.99 or more with a bottom edge that holds across frames, the success
// page's completion upper edge holding across frames), and the strip is then arithmetic on a fixed page geometry:
// `ResultsGuestPageGeometry` scales the 1080p reference lower bound by `screenHeight / 1080`, takes the measured
// anchor bottom plus `gap * scale` as the upper edge and clamps both into the screen. Width is not an input, and no
// control position is asked for. When the anchor cannot be measured, when the area is too short for the minimum
// board height, or when the screen size is nonsense, the answer is an explicit refusal and the vanilla page stays.
//
// Everything below runs against the real `ResultsGuestPageGeometry` of this build. The section at the end reads the
// production source instead, and says so.

internal static class Release11915Scenarios
{
    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        TheReferencePageIsTheFixedLowerBound(check);
        TheFailurePagePlacesUnderItsMeasuredTitle(check);
        TheSuccessPagePlacesUnderItsSettledCompletionEdge(check);
        AnAnchorThatCannotBeMeasuredIsNeverGuessed(check);
        TheLowerBoundScalesWithTheScreenHeightOnly(check);
        AnAreaTooShortForTheMinimumBoardIsRefused(check);
        AnImpossibleScreenIsRefused(check);
        TheMinimumBoardHeightIsTheOnlyBoardRule(check);
        Wiring(check);
    }

    // ------------------------------------------------------- the reference page itself
    //
    // 1080p is the page these numbers were measured on, so at that height the policy has to reproduce it exactly:
    // the lower bound is the screen minus the 8 pixel footer inset, and the upper edge is where the anchor ends.
    private static void TheReferencePageIsTheFixedLowerBound(Action<bool, string> check)
    {
        check(ResultsGuestPageGeometry.ReferenceWidth == 1920f
              && ResultsGuestPageGeometry.ReferenceHeight == 1080f
              && ResultsGuestPageGeometry.ReferenceBottomInset == 8f,
            "the fixed page geometry is stated in one place, as a 1920x1080 page with an 8 pixel footer inset");
    }

    // ------------------------------------------------------- the failure page, 1080p
    private static void TheFailurePagePlacesUnderItsMeasuredTitle(Action<bool, string> check)
    {
        bool resolved = ResultsGuestPageGeometry.TryResolve(380f, 1920f, 1080f, 16f, 96f, out ResultsSafeArea area);
        check(resolved, "a measured failure title at 380 on a 1080p page resolves a strip");
        check(resolved && Math.Abs(area.Top - 396f) < 0.01f,
            $"the upper edge is the measured title bottom plus the page gap: top={(resolved ? area.Top : float.NaN):0.##}");
        check(resolved && Math.Abs(area.Bottom - 1072f) < 0.01f,
            $"the lower edge is the screen minus the 8 pixel footer inset: bottom={(resolved ? area.Bottom : float.NaN):0.##}");
        check(resolved && Math.Abs(area.Height - 676f) < 0.01f,
            $"the strip is the whole distance between them: height={(resolved ? area.Height : float.NaN):0.##}");
    }

    // The success page's completion section ends lower, so the same page offers a shorter but real strip.
    private static void TheSuccessPagePlacesUnderItsSettledCompletionEdge(Action<bool, string> check)
    {
        bool resolved = ResultsGuestPageGeometry.TryResolve(520f, 1920f, 1080f, 16f, 96f, out ResultsSafeArea area);
        check(resolved, "a settled completion edge at 520 on a 1080p page resolves a strip");
        check(resolved && Math.Abs(area.Top - 536f) < 0.01f,
            $"the upper edge is the settled completion edge plus the page gap: top={(resolved ? area.Top : float.NaN):0.##}");
        check(resolved && Math.Abs(area.Bottom - 1072f) < 0.01f && Math.Abs(area.Height - 536f) < 0.01f,
            $"the same page lower bound applies, so the success strip is shorter by exactly the completion edge's own offset: bottom={(resolved ? area.Bottom : float.NaN):0.##} height={(resolved ? area.Height : float.NaN):0.##}");
    }

    // ------------------------------------------------------- an anchor nobody could read
    //
    // A title that is still fading in, a component that has not been laid out yet, a failed read: all of them hand
    // over a non-finite bottom edge. None of them may be turned into a placement, because there is nothing to place
    // against and the vanilla page is the honest answer.
    private static void AnAnchorThatCannotBeMeasuredIsNeverGuessed(Action<bool, string> check)
    {
        check(!ResultsGuestPageGeometry.TryResolve(float.NaN, 1920f, 1080f, 16f, 96f, out _),
            "an anchor bottom that reads NaN places no board");
        check(!ResultsGuestPageGeometry.TryResolve(float.PositiveInfinity, 1920f, 1080f, 16f, 96f, out _),
            "an anchor bottom that reads positive infinity places no board");
    }

    // ------------------------------------------------------- the same page at another resolution
    //
    // Height is the only thing the page is proportional to: the 1080p page scaled by `screenHeight / 1080`. An
    // ultra-wide screen changes nothing at all, and a narrower, shorter one moves both edges by exactly the scale.
    private static void TheLowerBoundScalesWithTheScreenHeightOnly(Action<bool, string> check)
    {
        bool ultrawide = ResultsGuestPageGeometry.TryResolve(380f, 2560f, 1080f, 16f, 96f, out ResultsSafeArea wide);
        check(ultrawide && Math.Abs(wide.Top - 396f) < 0.01f && Math.Abs(wide.Bottom - 1072f) < 0.01f,
            $"an ultra-wide 2560x1080 page resolves the same strip as 1920x1080: width is not an input top={(ultrawide ? wide.Top : float.NaN):0.##} bottom={(ultrawide ? wide.Bottom : float.NaN):0.##}");

        bool smaller = ResultsGuestPageGeometry.TryResolve(380f, 1280f, 1024f, 16f, 96f, out ResultsSafeArea narrow);
        check(smaller && Math.Abs(narrow.Top - 395.17f) < 0.01f,
            $"a 1280x1024 page scales the gap by 1024/1080 with the page: top={(smaller ? narrow.Top : float.NaN):0.00}");
        check(smaller && Math.Abs(narrow.Bottom - 1016.42f) < 0.01f,
            $"and scales the footer inset by the same factor: bottom={(smaller ? narrow.Bottom : float.NaN):0.00}");
        check(smaller && narrow.Top >= 0f && narrow.Top < narrow.Bottom && narrow.Bottom <= 1024f,
            $"the scaled strip stays inside the screen it was measured for: top={(smaller ? narrow.Top : float.NaN):0.00} bottom={(smaller ? narrow.Bottom : float.NaN):0.00}");
    }

    // ------------------------------------------------------- a strip too short for the board
    private static void AnAreaTooShortForTheMinimumBoardIsRefused(Action<bool, string> check)
    {
        check(!ResultsGuestPageGeometry.TryResolve(1060f, 1920f, 1080f, 16f, 450f, out _),
            "an anchor that leaves less room than the minimum board height resolves no strip, so the vanilla page stays");
    }

    // ------------------------------------------------------- a screen nobody could be looking at
    private static void AnImpossibleScreenIsRefused(Action<bool, string> check)
    {
        check(!ResultsGuestPageGeometry.TryResolve(380f, 1920f, 0f, 16f, 96f, out _),
            "a zero-height screen places no board");
        check(!ResultsGuestPageGeometry.TryResolve(380f, 1920f, 8f, 16f, 96f, out _),
            "a screen exactly as tall as the footer inset places no board");
        check(!ResultsGuestPageGeometry.TryResolve(380f, 1920f, float.NaN, 16f, 96f, out _),
            "an unreadable screen height places no board");
        check(!ResultsGuestPageGeometry.TryResolve(380f, 4f, 1080f, 16f, 96f, out _),
            "a screen four pixels wide places no board");
        check(!ResultsGuestPageGeometry.TryResolve(380f, 1920f, 1080f, -1f, 96f, out _),
            "a negative gap places no board rather than moving the upper edge upwards");
        check(!ResultsGuestPageGeometry.TryResolve(380f, 1920f, 1080f, 16f, 0f, out _),
            "a zero minimum board height places no board, because a board that cannot be drawn is not a placement");
    }

    // ------------------------------------------------------- the host/guest boundary
    //
    // The strip a guest page gets is 676 high on 1080p. A board that needs 700 does not fit it and the policy says
    // so; a board that needs exactly 676 fits it to the pixel. This is the one number that decides whether a strip
    // is offered at all, and it is decided by the board's own minimum height rather than by any control on the page.
    private static void TheMinimumBoardHeightIsTheOnlyBoardRule(Action<bool, string> check)
    {
        check(!ResultsGuestPageGeometry.TryResolve(380f, 1920f, 1080f, 16f, 700f, out _),
            "a board that needs 700 pixels is refused by a 676 pixel strip");
        check(ResultsGuestPageGeometry.TryResolve(380f, 1920f, 1080f, 16f, 676f, out _),
            "a board that needs exactly 676 pixels fits the 676 pixel strip it is offered");
    }

    // ------------------------------------------------------- the wiring, read from the production source
    //
    // A static source check only: it says the two guest branches of the real page really call this policy, that the
    // old proof-of-emptiness path is no longer on the guest route, and that no second copy of the arithmetic exists.
    // It is not a runtime behaviour test and does not pretend to be one.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.15 regression locates the production sources");
        if (root == null) return;

        string geometryPath = Path.Combine(root, "GuestPageGeometry.cs");
        check(File.Exists(geometryPath), "the fixed guest page geometry lives in its own source file");
        if (!File.Exists(geometryPath)) return;
        string geometry = File.ReadAllText(geometryPath);
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));

        check(geometry.Contains("class ResultsGuestPageGeometry"),
            "that file really declares the policy this suite drives: ResultsGuestPageGeometry");

        // Both guest pages place from the same policy, once each: the success page's completion branch and the
        // failure page's title branch. Any further call would be a second, divergent page geometry.
        int uses = 0;
        int at = -1;
        while ((at = results.IndexOf("ResultsGuestPageGeometry.TryResolve", at + 1, StringComparison.Ordinal)) >= 0) uses++;
        check(uses == 2,
            $"the guest page geometry is resolved exactly twice in ResultsDisplay.cs - once for the success page and once for the failure page: calls={uses}");

        // And nowhere else. `Placement` and `AnchorSample` both used to re-derive the strip for themselves; both now
        // read the one the page branch already computed, so the edges they compare are the edges that were placed.
        string placement = MethodBody(results, "private static bool Placement(Component page, bool succeeded");
        string anchorSample = MethodBody(results, "private static bool AnchorSample(Component page, bool succeeded");
        check(placement != null && !placement.Contains("ResultsGuestPageGeometry.TryResolve"),
            "Placement no longer derives a guest strip of its own");
        check(anchorSample != null && !anchorSample.Contains("ResultsGuestPageGeometry.TryResolve"),
            "AnchorSample compares against the strip the page branch computed instead of deriving a second one");

        // The old guest proofs are still called - but only on the host side of a guest test. Each call sits in the
        // final `else` of a chain that opens with `if (GuestClient())`, so a guest page can never reach it whatever
        // its interaction scan says. The test is structural - the guest branch has closed before the call - rather
        // than a fixed character distance, because the chain between the two grew a `else if (…Measured)` arm.
        check(GuardedFromGuestBranch(results, "FailureNoFooter.Observe")
              && CountOf(results, "FailureNoFooter.Observe") == 1,
            "FailureNoFooter.Observe is called exactly once, and only in the branch a guest page never takes");
        check(GuardedFromGuestBranch(results, "SuccessNoFooter.Observe")
              && CountOf(results, "SuccessNoFooter.Observe") == 1,
            "SuccessNoFooter.Observe is called exactly once, and only in the branch a guest page never takes");

        // 1.19.14 cleared the button settle state before each no-footer observation. Dropping that call left the
        // Footer's stable count alive across unmeasured frames: A->B->A settled on a single reading, and
        // Footer.Exhausted never became true, so the "[DIAG] ... no stable native footer" line could never print.
        // Both guest branches keep a reset of their own, both host no-footer branches must keep theirs.
        const string settleReset = "ButtonSettled(false, float.NaN)";
        int settleResets = CountOf(results, settleReset);
        check(settleResets >= 4,
            $"both guest branches and both host no-footer branches clear the button settle state: resets={settleResets}");
        foreach (string observe in new[] { "FailureNoFooter.Observe", "SuccessNoFooter.Observe" })
        {
            int call = results.IndexOf(observe, StringComparison.Ordinal);
            int from = call < 0 ? 0 : Math.Max(0, call - 400);
            check(call >= 0 && results.Substring(from, call - from).Contains(settleReset),
                $"{observe} is preceded by the 1.19.14 settle reset, so Footer.Exhausted stays reachable on the host no-footer path");
        }

        // `BoardStillSafe` re-measures the page on every repaint. Every proof-of-emptiness it reads is guarded by the
        // same guest test, so the per-frame re-measurement can never restore the gate that refused the board.
        string stillSafe = MethodBody(results, "private static bool BoardStillSafe()");
        check(stillSafe != null, "BoardStillSafe is where the per-repaint re-measurement lives");
        int proofs = 0;
        bool guarded = stillSafe != null;
        int cursor = 0;
        while (stillSafe != null)
        {
            int proof = stillSafe.IndexOf("ProvenEmpty", cursor, StringComparison.Ordinal);
            if (proof < 0) break;
            proofs++;
            int from = Math.Max(0, proof - 200);
            if (!stillSafe.Substring(from, proof - from).Contains("GuestClient()")) guarded = false;
            cursor = proof + 1;
        }
        check(proofs > 0 && guarded,
            $"every ProvenEmpty inside BoardStillSafe is guarded by the guest test, so a guest page is never held back by a proof it cannot have: proofs={proofs}");

        Console.WriteLine("WIRING: RESULT_GUEST_PAGE_GEOMETRY (static source check; not a runtime behaviour test)");
    }

    private static int CountOf(string source, string needle)
    {
        int count = 0;
        int at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }

    // A call a guest page must never reach: the nearest `if (GuestClient())` above it opens a braced branch that has
    // already closed by the time the call is reached, so the call sits in the `else` side of that chain. Brace
    // counting only; it says which branch a call is written in, never what that branch means.
    private static bool GuardedFromGuestBranch(string source, string needle)
    {
        int at = source.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return false;
        int gate = source.LastIndexOf("if (GuestClient())", at, StringComparison.Ordinal);
        if (gate < 0) return false;
        int open = source.IndexOf('{', gate);
        if (open < 0 || open > at) return false;
        int depth = 0;
        for (int index = open; index < at; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return true;
            }
        }
        return false;
    }

    // The body of the one method whose signature starts with `signature`, brace for brace. A source check, not a
    // compiler: it is only ever asked whether a call is inside a method, never what that method means.
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        int open = source.IndexOf('{', start);
        if (open < 0) return null;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, index - open + 1);
            }
        }
        return null;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
