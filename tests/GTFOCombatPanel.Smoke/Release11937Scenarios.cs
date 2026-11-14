using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.37 regressions, driven through the production policy and read back out of the production sources.
//
//   (1) the machine's own blood loss was always 0 because the live player carries Dam_PlayerDamageLocal, which overrides
//       the base's damage methods. Both declaring types are now read and deduplicated.
//   (2) a host failure page's own title-to-footer band was withdrawn when its leave button was unreadable. The failure
//       band proof preserves the complete-scan and overlap refusals.
//   (3) a host success page with a visible native footer and unreadable button can prove its own IconRow-to-footer band.
//       Screen edges, incomplete scans, active/unmeasurable interactions and overlaps remain refusals.
//
// What cannot be asserted here: Unity, Harmony, the interop assembly and the real dispatch. Damage target discovery uses
// stand-in types, while result-page wiring is checked against the production source and policy is run headlessly.
// Neither path claims to verify native/Unity behaviour.
internal static class Release11937Scenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        // ---- defect 1: the player's own damage methods ------------------------------------------------------------
        TheDeclaringTypesAreTheBaseAndTheLocalSubclass(check);
        AMethodDeclaredOnlyOnTheSubclassIsObservedAndSeeded(check);
        ADegradedProfileStillObservesTheBase(check);
        OneMethodIsDetouredOnce(check);
        ARepeatedObservationCreditsNothing(check);
        TheNameRulesApplyToBothTypes(check);
        TheObservationWiring(check);
        // ---- defect 2: the host failure page's own band ------------------------------------------------------------
        TheRecordedHostPageIsProofEnough(check);
        AnActiveOverlappingControlStillDefers(check);
        AScanThatCouldNotSeeThePageStillRefuses(check);
        ABandThatIsNotThePagesOwnIsNotProof(check);
        TheFallbackBoardSurvivesTheSecondLook(check);
        TheNoFooterProofIsStillClientOnly(check);
        TheFailureBandWiring(check);
        // ---- defect 3: success page's page-owned band proof ---------------------------------------------------------
        TheRecordedSuccessPageIsProofEnough(check);
        SuccessBandProofRejectsMissingEvidence(check);
        TheSuccessBandWiring(check);
        ProofStateDiagnosticsAreUnconditionalAndPerPage(check);
        RemeasureWordingNamesTheActualProbe(check);
    }

    // ---- defect 1 ------------------------------------------------------------------------------------------------

    // The interop's own shape, as a stand-in pair: the base declares a damage report, the health write and the one method
    // the mod deliberately leaves alone; the subclass overrides two of them with its own native methods and adds one of
    // its own, which is the case the recorded run hit (`ReceiveExplosionDamage`, the mine blast).
    private class DamageProbeBase
    {
        public virtual void ReceiveBulletDamage() { }
        public virtual void ReceiveParasiteDamage() { }
        public void ReceiveAddHealth() { }
        public virtual void Setup() { }
        public int NotVoid() => 0;
    }

    private sealed class DamageProbeLocal : DamageProbeBase
    {
        public override void ReceiveBulletDamage() { }
        public override void Setup() { }
        // Declared *only* here, exactly like the subclass' own `ReceiveExplosionDamage`.
        public void ReceiveExplosionDamage() { }
        public override void ReceiveParasiteDamage() { }
    }

    private static void TheDeclaringTypesAreTheBaseAndTheLocalSubclass(Action<bool, string> check)
    {
        Type[] pair = HealthLossSources.DeclaringTypes(typeof(DamageProbeBase), typeof(DamageProbeLocal));
        check(pair.Length == 2 && pair[0] == typeof(DamageProbeBase) && pair[1] == typeof(DamageProbeLocal),
            "the target list covers the base *and* the subclass a live player really carries, base first: " + HealthLossSources.Describe(pair));
        check(HealthLossSources.LocalTypeName == "Dam_PlayerDamageLocal",
            "the subclass the patch looks for by name is Dam_PlayerDamageLocal, the only subclass in the interop assembly");
        // A type that is not the base's subclass is not a declaring type: the list would otherwise hand Harmony a method
        // of some unrelated type, and `__instance` would not be a damage base at all.
        check(HealthLossSources.DeclaringTypes(typeof(DamageProbeBase), typeof(Version)).Length == 1,
            "a type that is not a subclass of the damage base contributes nothing");
        check(HealthLossSources.DeclaringTypes(typeof(DamageProbeBase), typeof(DamageProbeBase)).Length == 1,
            "and the base named twice is still one declaring type");
    }

    private static void AMethodDeclaredOnlyOnTheSubclassIsObservedAndSeeded(Action<bool, string> check)
    {
        Type[] pair = HealthLossSources.DeclaringTypes(typeof(DamageProbeBase), typeof(DamageProbeLocal));
        var names = new List<string>();
        Type localMethodType = null;
        foreach (System.Reflection.MethodBase method in HealthLossSources.DeclaredMethods(pair, HealthLossTargets.Accepts))
        {
            names.Add(method.Name);
            if (method.Name == "ReceiveExplosionDamage") localMethodType = method.DeclaringType;
        }
        check(names.Contains("ReceiveExplosionDamage") && localMethodType == typeof(DamageProbeLocal),
            "a damage report declared only on the subclass is in the observed list, and it is the subclass' own method: " + string.Join(",", names));
        check(names.Contains("ReceiveBulletDamage") && names.Contains("Setup") && names.Contains("ReceiveAddHealth"),
            "and the base's own accepted methods are still observed beside it");
        check(!names.Contains("ReceiveParasiteDamage"),
            "the parasite-damage exclusion still removes that call, on the subclass as on the base");
        check(!names.Contains("NotVoid"),
            "a method that is not void is never detoured: a detour would have to invent a return value");

        // The subclass' own report is a blow in the ledger's own terms - it is seeded before the call and credited by the
        // postfix - which is the whole of the recorded host row being 0.
        var ledger = new HealthLossLedger();
        IntPtr seat = (IntPtr)0x11937;
        check(HealthLossTargets.Accepts("ReceiveExplosionDamage") && HealthLossTargets.Seedable("ReceiveExplosionDamage"),
            "the subclass' own damage report is accepted by the one name rule and may plant the pre-call mark");
        double loss = Call(ledger, "ReceiveExplosionDamage", seat, 0xA1UL, 25.0, 18.5, 25.0);
        check(Math.Abs(loss - 6.5) < 1e-9,
            "so a mine blast that takes 6.5 HP off a full-health player is credited in full: " + loss.ToString("0.###"));
        check(Math.Abs(Call(ledger, "ReceiveExplosionDamage", seat, 0xA1UL, 18.5, 12.0, 25.0) - 6.5) < 1e-9,
            "and the next one is measured from where the last one left the mark");
    }

    private static void ADegradedProfileStillObservesTheBase(Action<bool, string> check)
    {
        // The interop of a profile that does not carry the subclass: the resolution answers null and the list is the base
        // alone. Nothing may throw here - a patch class that fails to register takes the whole metric with it.
        Type[] degraded = HealthLossSources.DeclaringTypes(typeof(DamageProbeBase), null);
        check(HealthLossSources.Describe(degraded) == "DamageProbeBase only",
            "and the degraded set says so in one word, which is the line the patch writes when the subclass is missing");
        check(degraded.Length == 1 && degraded[0] == typeof(DamageProbeBase),
            "an unresolvable subclass degrades the target list to the base alone");
        var names = new List<string>();
        bool threw = false;
        try
        {
            foreach (System.Reflection.MethodBase method in HealthLossSources.DeclaredMethods(degraded, HealthLossTargets.Accepts))
                names.Add(method.Name);
        }
        catch (Exception) { threw = true; }
        check(!threw && names.Contains("ReceiveBulletDamage") && names.Contains("Setup") && !names.Contains("ReceiveExplosionDamage"),
            "and that degraded list is still a full base list, without a subclass method and without an exception: " + string.Join(",", names));
        check(HealthLossSources.DeclaringTypes(null, typeof(DamageProbeLocal)).Length == 0,
            "no base type at all is an empty list, never an exception");
        var empty = new List<string>();
        threw = false;
        try
        {
            foreach (System.Reflection.MethodBase method in HealthLossSources.DeclaredMethods(
                HealthLossSources.DeclaringTypes(null, typeof(DamageProbeLocal)), HealthLossTargets.Accepts))
                empty.Add(method.Name);
        }
        catch (Exception) { threw = true; }
        check(!threw && empty.Count == 0, "an empty declaring-type list yields no targets and does not throw");
        threw = false;
        try
        {
            foreach (System.Reflection.MethodBase _ in HealthLossSources.DeclaredMethods(null, HealthLossTargets.Accepts)) { }
        }
        catch (Exception) { threw = true; }
        check(!threw, "a null list of declaring types yields nothing instead of throwing");
    }

    private static void OneMethodIsDetouredOnce(Action<bool, string> check)
    {
        // The same declaring type twice: the dedupe key is the method's own metadata identity, so the list is unchanged.
        var twice = new List<System.Reflection.MethodBase>();
        foreach (System.Reflection.MethodBase method in HealthLossSources.DeclaredMethods(
            new[] { typeof(DamageProbeLocal), typeof(DamageProbeLocal) }, HealthLossTargets.Accepts))
            twice.Add(method);
        var distinct = new HashSet<string>();
        foreach (System.Reflection.MethodBase method in twice) distinct.Add(method.DeclaringType.Name + "." + method.Name);
        check(twice.Count > 0 && twice.Count == distinct.Count,
            "a method is handed to Harmony exactly once, whatever the declaring-type list repeats: " + twice.Count + " target(s), " + distinct.Count + " distinct");

        var pair = new List<System.Reflection.MethodBase>();
        foreach (System.Reflection.MethodBase method in HealthLossSources.DeclaredMethods(
            HealthLossSources.DeclaringTypes(typeof(DamageProbeBase), typeof(DamageProbeLocal)), HealthLossTargets.Accepts))
            pair.Add(method);
        var both = new HashSet<string>();
        foreach (System.Reflection.MethodBase method in pair) both.Add(method.DeclaringType.Name + "." + method.Name);
        check(pair.Count == both.Count,
            "the base and the subclass together produce no duplicate either (DeclaredOnly sets are disjoint): " + pair.Count + " target(s)");
    }

    private static void ARepeatedObservationCreditsNothing(Action<bool, string> check)
    {
        // A subclass override that calls its base implementation is observed twice for one drop. The watermark is what
        // makes that safe, and this is the assertion that says so: the second observation of the same figure credits 0
        // and leaves the credited total exactly where the first one put it.
        var ledger = new HealthLossLedger();
        IntPtr seat = (IntPtr)0x5150;
        ledger.Seed(seat, 0xB2UL, 25.0, 25.0);
        double first = ledger.Observe(seat, 0xB2UL, 23.74533, 25.0);
        double credited = first;
        double second = ledger.Observe(seat, 0xB2UL, 23.74533, 25.0);
        credited += second;
        check(Math.Abs(first - 1.25467) < 1e-9 && second == 0.0,
            "the first observation credits the drop and the nested second one credits nothing: first="
            + first.ToString("0.#####") + ", second=" + second.ToString("0.#####"));
        check(Math.Abs(credited - first) < 1e-12,
            "and the credited total is unchanged by the repeat: " + credited.ToString("0.#####"));
        check(ledger.Marked == 1, "one seat, one mark: " + ledger.Marked);
    }

    private static void TheNameRulesApplyToBothTypes(Action<bool, string> check)
    {
        // The same name rule is asked per name and never per type, so what holds for the base holds for the subclass.
        check(!HealthLossTargets.Accepts("ReceiveParasiteDamage") && !HealthLossTargets.Seedable("ReceiveParasiteDamage"),
            "the parasite-damage exclusion is a rule about the name, so it removes the subclass' override too");
        check(HealthLossTargets.Seedable("ReceiveTentacleGrabStop") && HealthLossTargets.Accepts("ReceiveTentacleGrabStop"),
            "and the ReceiveTentacleGrabStop exception is the same rule on either type");
        check(HealthLossTargets.Accepts("Setup") && !HealthLossTargets.Seedable("Setup"),
            "a health write declared on both types is observed on both and may never plant a mark");
        check(HealthLossTargets.Accepts("ReceiveSetHealth") && !HealthLossTargets.Seedable("ReceiveSetHealth"),
            "the replicated health write the recorded run's spawn used is observed and never seeded");
    }

    private static void TheObservationWiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.37 blood-loss regression locates the production sources");
        if (root == null) return;

        string patches = Code(File.ReadAllText(Path.Combine(root, "HealthLossPatches.cs")));
        string ledger = Code(File.ReadAllText(Path.Combine(root, "HealthLossLedger.cs")));

        check(patches.Contains("=> HealthLossSources.DeclaredMethods(DeclaringTypes(), accept);"),
            "the target list is built from the declaring-type list, not from one type's reflection");
        check(patches.Contains("=> HealthLossSources.DeclaringTypes(typeof(Dam_PlayerDamageBase), LocalDamageType());"),
            "and that list is the base plus the subclass resolved at run time");
        check(patches.Contains("BindingFlags.DeclaredOnly") == false && ledger.Contains("BindingFlags.DeclaredOnly"),
            "DeclaredOnly is read in the one place that enumerates the types, so the two sources can never overlap");
        check(ledger.Contains("var seen = new HashSet<MethodBase>(MethodIdentity.Instance);") && ledger.Contains("if (!seen.Add(method)) continue;"),
            "the dedupe key is the MethodBase itself, so one method can never be patched twice");
        check(ledger.Contains("if (left.DeclaringType != right.DeclaringType) return false;") && ledger.Contains("return Token(left) == Token(right);"),
            "two MethodBase are the same method by declaring type and metadata token, which is the identity Harmony keys on");
        check(patches.Contains("private static Type ResolveLocalDamageType()")
              && patches.Contains("Type direct = assembly.GetType(HealthLossSources.LocalTypeName, false);"),
            "the subclass is resolved by name out of the interop assembly, so a profile without it still builds");
        check(patches.Contains("_localTypeMissingLogged") && patches.Contains("Plugin.Logger?.LogWarning("),
            "a missing subclass is reported once, on one bounded line, instead of failing the patch class");
        check(patches.Contains("_localDamageType = ResolveLocalDamageType();") && patches.Contains("return _localDamageType;"),
            "the resolution is cached, and a null answer is cached too - the degraded list is not re-resolved per frame");
        check(patches.Contains("HealthLossPatches.DamageBaseMethods(HealthLossTargets.Seedable)"),
            "the prefix is still attached to the damage reports and to nothing else");
        check(patches.Contains("private static IEnumerable<MethodBase> TargetMethods() => DamageBaseMethods(HealthLossTargets.Accepts);"),
            "and the postfix still observes every accepted method of both types");
        check(CountOf(patches, "Ledger.Seed(") == 1 && CountOf(patches, "Ledger.Observe(") == 1,
            "one seed entry point and one observation, whichever declaring type the call arrived through");
    }

    // ---- defect 2 ------------------------------------------------------------------------------------------------

    // The recorded page, as `FailureStrip` measured it: 2560x1440, the mission-failed title's own edge at 381, the waiting
    // text it laid out at 1164 (the band's foot, a design clearance above it, comes out at 1142), all five of the page's
    // controls switched off, the complete scan finding nothing, the native page reporting its footer visible - and the
    // leave button's rectangle unreadable, because `ButtonActive` refuses a button the page switched off.
    private static FailureBandEvidence RecordedHostPage()
        => new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
            ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText);

    private static void TheRecordedHostPageIsProofEnough(Action<bool, string> check)
    {
        FailureBandEvidence evidence = RecordedHostPage();
        FailureBandProofState state = ResultFailureBandProofPolicy.Decide(evidence);
        check(state == FailureBandProofState.Proven && ResultFailureBandProofPolicy.Proven(evidence),
            "the recorded shape - complete scan, nothing switched on, the page's own measured band - is proof enough, so the board is neither refused nor withdrawn: " + ResultFailureBandProofPolicy.Name(state));
        check(ResultFailureBandProofPolicy.Name(state) != "no-footer-unproofed",
            "and the reason that withdrew it (`no-footer-unproofed`) is not the answer this shape gets");
        check(ResultFailureBandProofPolicy.Name(state) == "page-band-proven",
            "and the reason carries its own word, so a live log can tell this release from the button's and from the client's");
        check(ResultFailureBandProofPolicy.Describe(state, new ResultsBand(381f, 1142f, ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText, false))
                .Contains("bandBottom=footer-text"),
            "the line the refusal used to print now names which of the page's own facts produced each edge");
        // The two other ways the same page can be released are unchanged: a readable button, and a client's fixed geometry.
        check(ResultFailureBandProofPolicy.Proven(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterTextControl)) &&
              ResultFailureBandProofPolicy.Proven(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.MeasuredControl)),
            "a waiting line drawn by the page as a control, or any other control it measured below the board, is the same proof");
        check(ResultFailureBandProofPolicy.Proven(new FailureBandEvidence(ResultPageRole.Host, true, true, 2, 0, true, true, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.ScreenFoot)) == false,
            "and a button the page did hand over does not release a page whose scan still saw two active controls");
        check(ResultFailureBandProofPolicy.Proven(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, false, true, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterControl)),
            "while a band bounded by the button itself is still the ordinary release, which this release does not touch");
    }

    private static void AnActiveOverlappingControlStillDefers(Action<bool, string> check)
    {
        FailureBandEvidence evidence = new FailureBandEvidence(ResultPageRole.Host, true, true, 1, 0, true, false, true,
            ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText);
        FailureBandProofState state = ResultFailureBandProofPolicy.Decide(evidence);
        check(state == FailureBandProofState.OverlapsControls && !ResultFailureBandProofPolicy.Proven(evidence),
            "an active control whose rectangle reaches the board still defers the frame, whatever else was measured");
        check(ResultFailureBandProofPolicy.Name(state) == "overlaps-controls",
            "and it says so in the one word the display's own deferral line uses");
        check(ResultFailureBandProofPolicy.Name(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(
                ResultPageRole.Host, true, true, 1, 0, true, false, false, ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)))
                == "active-controls",
            "and a control that is switched on but does not reach the board still means the page is not empty");
    }

    private static void AScanThatCouldNotSeeThePageStillRefuses(Action<bool, string> check)
    {
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Host, false, true, 0, 0, true, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)) == FailureBandProofState.ScanIncomplete,
            "a scan that could not see the whole page proves nothing: no relaxation for an incomplete scan");
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Host, true, false, 0, 0, true, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)) == FailureBandProofState.ScanIncomplete,
            "nor for a scan whose action parent could not be covered");
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 1, true, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)) == FailureBandProofState.ActiveControls,
            "an active control whose rectangle could not be read is still an interaction nobody could place");
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, false, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)) == FailureBandProofState.NativeFooterAbsent,
            "a page that reports no footer of its own is not the shape this proof is about");
    }

    private static void ABandThatIsNotThePagesOwnIsNotProof(Action<bool, string> check)
    {
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.ScreenSafeArea, ResultBoundSource.FooterText)) == FailureBandProofState.BandNotPageMeasured,
            "an upper edge taken from a fraction of the window is not this page's geometry");
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.ScreenFoot)) == FailureBandProofState.BandNotPageMeasured,
            "nor is a lower edge that is the window's own foot: nothing under the board was measured there");
        check(ResultFailureBandProofPolicy.Decide(new FailureBandEvidence(ResultPageRole.Client, true, true, 0, 0, false, false, false,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)) == FailureBandProofState.ClientOwned,
            "a client's strip is its fixed page geometry, so this proof is the host's and never the client's");
        check(!ResultFailureBandProofPolicy.PageMeasuredTop(ResultBoundSource.None)
              && !ResultFailureBandProofPolicy.PageMeasuredBottom(ResultBoundSource.None)
              && !ResultFailureBandProofPolicy.PageMeasuredBottom(ResultBoundSource.ScreenFoot),
            "an unmeasured edge and the window's foot are both outside the page-measured set");
    }

    // The recorded sequence, frame by frame, through the same calls the display makes: a fail-open board, the page's one
    // second look (whose strip the button really was measurable in), the strict board that look produced - and then the
    // frame that cannot read that button again. That frame is the one that withdrew the board; it must now keep it.
    private static void TheFallbackBoardSurvivesTheSecondLook(Action<bool, string> check)
    {
        // Frame 0: the fail-open placement. Its band is the window's safe area, so nothing about it is the page's own
        // measurement and no board may be placed strictly from it.
        var fallbackBand = new ResultsBand(381f, 1142f, ResultBoundSource.ScreenSafeArea, ResultBoundSource.ScreenFoot, false);
        check(!ResultFailureBandProofPolicy.Proven(new FailureBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.ScreenSafeArea, ResultBoundSource.ScreenFoot)),
            "the fail-open band itself is never proof: it starts from a fraction of the window and ends at its foot");

        // The page's one second look: the strip the strict placement would take is offerable and its footer was measured
        // on this frame (`ResultAnchorRecovery.Recovered`), so the recovery is spent.
        int recoveries = 0;
        check(ResultAnchorRecovery.ShouldRetry(recoveries, true, true) && ResultAnchorRecovery.Recovered(true, true, false),
            "the second look is taken on a strip whose footer this frame really measured");
        recoveries = ResultAnchorRecovery.Record(recoveries);

        // The frame after it: the strict board stands at 381..1145, and the button cannot be read this frame. This is the
        // recorded `failure-strip board not drawn` frame.
        FailureBandEvidence afterTheLook = RecordedHostPage();
        check(ResultFailureBandProofPolicy.Proven(afterTheLook),
            "the repaint after the second look keeps the board: the page's own band is still measured, and the button it cannot read is not a reason to withdraw it");
        // The same frame with a control that really is switched on and reaches the board: that still defers, which is what
        // keeps the withdrawal path alive for a page that has something to protect.
        check(!ResultFailureBandProofPolicy.Proven(new FailureBandEvidence(ResultPageRole.Host, true, true, 1, 0, true, false, true,
                ResultBoundSource.TitleAnchor, ResultBoundSource.FooterText)),
            "while a page that really exposes an active control under the board still defers on that same frame");
        // And the second look can never be spent twice: the deferral does not refund it (1.19.26's fix).
        check(!ResultAnchorRecovery.ShouldRetry(recoveries, true, true),
            "and the second look stays spent, so the page cannot loop fail-open -> second look -> withdraw");
    }

    private static void TheNoFooterProofIsStillClientOnly(Action<bool, string> check)
    {
        check(!ResultPageRolePolicy.UsesNoFooterSafeArea(ResultPageRole.Host)
              && ResultPageRolePolicy.UsesNoFooterSafeArea(ResultPageRole.Client),
            "the no-footer safe area is still a client's alone");
        var gate = new ResultNoFooterGate();
        bool ready = false;
        for (int frame = 0; frame < 6; frame++)
            ready |= gate.Observe(frame, false, true, true, false, true, 0, 0);
        check(!ready && !gate.Ready,
            "and `FailureNoFooter.Ready` still answers false for a host however perfect its scan is - the client gate is untouched");
        ready = false;
        for (int frame = 0; frame < 6; frame++)
            ready |= gate.Observe(frame, true, true, true, false, true, 0, 0);
        check(ready && gate.Ready,
            "while the same page as a client is released by it, exactly as before");
    }

    private static void TheFailureBandWiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.37 failure-page regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "ResultsPagePolicy.cs"));

        check(policy.Contains("bool absent = client && anchorSettled && nativeStateKnown && !nativeFooterVisible"),
            "the client gate on the no-footer proof is still exactly where it was");
        check(display.Contains("if (!failureNoFooterReady && !failureBandProven)"),
            "and the host's release is now the union of that proof and the page's own band, not the client proof alone");

        string stillSafe = MethodBody(Code(display), "private static bool BoardStillSafe()");
        check(stillSafe != null, "the per-repaint check is found in the production source");
        if (stillSafe != null)
        {
            int overlap = stillSafe.IndexOf("failureControls.OverlapsBoard", StringComparison.Ordinal);
            int overlapReport = stillSafe.IndexOf("ReportBoardOverlap(\"failure\"", StringComparison.Ordinal);
            int bandProof = stillSafe.IndexOf("ResultFailureBandProofPolicy.Decide(", StringComparison.Ordinal);
            check(overlap >= 0 && overlapReport > overlap && bandProof > overlapReport,
                "the overlap refusal is asked before the band proof, so a control that reaches the board always defers first");
            check(stillSafe.Contains("\"no-footer-unproofed\""),
                "the refusal that withdrew the recorded board is still named, for a shape that is not proven");
            check(stillSafe.Contains("failureBandProof != FailureBandProofState.Proven"),
                "and a shape the policy does not prove is still refused: nothing here is a blanket release");
        }

        string placement = MethodBody(Code(display), "private static bool Placement(Component page, bool succeeded");
        check(placement != null && placement.Contains("ResultFailureBandProofPolicy.Decide(")
              && placement.Contains("bool failureBandProven = failureBandProof == FailureBandProofState.Proven;"),
            "the strict placement asks the same policy, so the page's own band can place the board instead of only keeping it");
        check(placement != null && placement.Contains("failure-page-band-released"),
            "and it says once, in one bounded line, which of the page's facts released it");
        check(placement != null && placement.Contains("if (failureNoFooterReady)")
              && placement.Contains("failureFooterSource = ResultFooterSource.ClientNoFooterSafeArea;"),
            "the client safe area is still handed out only on the client proof, never on a host's band");
        check(CountOf(Code(display), "FailureNoFooter.Observe(") == 1,
            "the no-footer gate is still observed exactly once per measurement");

        check(display.Contains("out ResultsBand band, LayoutRect overlapBoard = default, bool inspectBoard = false)")
              && display.Contains("band = new ResultsBand(top, bottom, bandTopSource, bandBottomSource, false);"),
            "`FailureStrip` hands out the band it measured, with the source of each edge, so the policy is asked about the real shape");
        check(CountOf(Code(display), "bandBottomSource = pageBand.BottomSource;") == 1
              && CountOf(Code(display), "bandBottomSource = ResultBoundSource.FooterControl;") == 1,
            "the lower edge's provenance is recorded where the edge is chosen - the page's band when it lowered the strip, the button when it bounded it");
    }

    private static SuccessBandEvidence RecordedSuccessPage()
        => new SuccessBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
            ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl);

    private static void TheRecordedSuccessPageIsProofEnough(Action<bool, string> check)
    {
        SuccessBandEvidence evidence = RecordedSuccessPage();
        SuccessBandProofState state = ResultSuccessBandProofPolicy.Decide(evidence);
        check(state == SuccessBandProofState.Proven && ResultSuccessBandProofPolicy.Proven(evidence),
            "the recorded success shape - host, complete coverage, empty scan, native footer visible, button unreadable, IconRow to FooterTextControl - proves its own band: " + ResultSuccessBandProofPolicy.Name(state));
        check(ResultSuccessBandProofPolicy.Name(state) != "no-footer-unproofed",
            "and the old no-footer refusal is not the result for a proven success-page band");
        check(ResultSuccessBandProofPolicy.Describe(state,
                new ResultsBand(350f, 1280f, ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl, false))
                .Contains("bandBottom=footer-text-control"),
            "the success proof description names the page-measured lower edge rather than a named button or screen edge");
    }

    private static void SuccessBandProofRejectsMissingEvidence(Action<bool, string> check)
    {
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.ScreenSafeArea, ResultBoundSource.FooterTextControl)) == SuccessBandProofState.BandNotPageMeasured,
            "a success band whose upper edge is ScreenSafeArea is not page-owned proof");
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, false, false,
                ResultBoundSource.IconRow, ResultBoundSource.ScreenFoot)) == SuccessBandProofState.BandNotPageMeasured,
            "a success band whose lower edge is ScreenFoot is not page-owned proof");
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, false, true, 0, 0, true, false, false,
                ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl)) == SuccessBandProofState.ScanIncomplete,
            "an incomplete interaction scan cannot prove the success band");
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, true, false, 0, 0, true, false, false,
                ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl)) == SuccessBandProofState.ScanIncomplete,
            "an incomplete parent-coverage scan cannot prove the success band");
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, true, true, 1, 0, true, false, false,
                ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl)) == SuccessBandProofState.ActiveControls,
            "an active control prevents a page-band proof even when it does not overlap");
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, true, true, 0, 1, true, false, false,
                ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl)) == SuccessBandProofState.ActiveControls,
            "an unmeasurable interaction prevents a page-band proof");
        SuccessBandProofState overlap = ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Client,
            false, false, 1, 1, true, false, true, ResultBoundSource.ScreenSafeArea, ResultBoundSource.ScreenFoot));
        check(overlap == SuccessBandProofState.OverlapsControls,
            "overlap is refused before role, scan completeness, control counts or band provenance are considered: " + ResultSuccessBandProofPolicy.Name(overlap));
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Client, true, true, 0, 0, false, false, false,
                ResultBoundSource.IconRow, ResultBoundSource.FooterTextControl)) == SuccessBandProofState.ClientOwned,
            "the host page-band proof does not change the guest/client route");
        check(ResultSuccessBandProofPolicy.Decide(new SuccessBandEvidence(ResultPageRole.Host, true, true, 0, 0, true, true, false,
                ResultBoundSource.ScreenSafeArea, ResultBoundSource.ScreenFoot)) == SuccessBandProofState.Proven,
            "the existing measured-button path remains valid without relying on the page-band alternative");
    }

    private static void TheSuccessBandWiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the success-page band regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string code = Code(display);
        string stillSafe = MethodBody(code, "private static bool BoardStillSafe()");
        string placement = MethodBody(code, "private static bool Placement(Component page, bool succeeded");
        string successRegion = MethodBody(code, "private static bool SuccessRegion(");
        check(stillSafe != null && placement != null && successRegion != null,
            "the safety, strict placement and current success-region measurement are found in production source");
        if (stillSafe == null || placement == null || successRegion == null) return;

        int overlap = stillSafe.IndexOf("if (successControls.OverlapsBoard)", StringComparison.Ordinal);
        int policy = stillSafe.IndexOf("ResultSuccessBandProofPolicy.Decide(", StringComparison.Ordinal);
        check(overlap >= 0 && policy > overlap && stillSafe.Contains("out ResultsBand successBand"),
            "BoardStillSafe checks the current-frame overlap first, then asks the success policy with the freshly measured band");
        check(stillSafe.Contains("successControls.ParentCoverageComplete")
              && stillSafe.Contains("ResultSuccessBandProofPolicy.Describe(successBandProof, successBand)"),
            "the per-repaint branch requires current parent coverage and reports its policy refusal");

        int placementPolicy = placement.IndexOf("ResultSuccessBandProofPolicy.Decide(", StringComparison.Ordinal);
        int placementOverlap = placement.IndexOf("if (successControls.OverlapsBoard)", StringComparison.Ordinal);
        check(placementPolicy >= 0 && placementOverlap >= 0 && placementPolicy > placementOverlap
              && placement.Contains("out ResultsBand successBand"),
            "strict placement checks overlap before the same success policy and consumes the band's live edge sources");
        check(placement.Contains("else if (measuredButton)") && placement.Contains("ButtonSettled(true, measuredTop)")
              && placement.Contains("if (GuestClient())")
              && placement.Contains("ResultNoFooterSafeArea.TryResolve(area.Top"),
            "the already-measured button and guest/client fixed-geometry routes remain separate and unchanged");
        check(placement.Contains("footerSource = ResultFooterSource.InteractiveBand;")
              && placement.Contains("success board released by the page's measured band")
              && !placement.Contains("footerSource = ResultFooterSource.NamedControl;"),
            "success band release is labeled as the measured band, never as a named button");

        check(successRegion.Contains("ResultBoundSource.IconRow") && successRegion.Contains("ResultBoundSource.LastTrustedIconRow")
              && successRegion.Contains("out ResultsBand band") && successRegion.Contains("pageBand = band;"),
            "the proof uses the actual SuccessRegion band built from the icon row and the measured footer, not a cached strip");
    }

    private static void ProofStateDiagnosticsAreUnconditionalAndPerPage(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "proof-state diagnostic source checks locate the production sources");
        if (root == null) return;
        string display = Code(File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs")));
        string placement = MethodBody(display, "private static bool Placement(Component page, bool succeeded");
        check(placement != null, "the placement proof-rejection body is found");
        if (placement == null) return;

        int failureLog = placement.IndexOf("HeadingReports.Add(\"failure-band-proof-state\")", StringComparison.Ordinal);
        int failureExhausted = placement.IndexOf("if (Footer.Exhausted && HeadingReports.Add(\"failure-footer-unsettled\"))", StringComparison.Ordinal);
        int successLog = placement.IndexOf("HeadingReports.Add(\"success-band-proof-state\")", StringComparison.Ordinal);
        int successExhausted = successLog >= 0
            ? placement.IndexOf("if (Footer.Exhausted && HeadingReports.Add(\"success-footer-unsettled\"))", successLog, StringComparison.Ordinal)
            : -1;
        check(failureLog >= 0 && failureExhausted > failureLog
              && placement.Contains("ResultFailureBandProofPolicy.Describe(failureBandProof, failureBand)"),
            "failure proof-state DIAG is per-page, includes Describe, and is emitted before the Footer.Exhausted-only legacy line");
        check(successLog >= 0 && successExhausted > successLog
              && placement.Contains("ResultSuccessBandProofPolicy.Describe(successBandProof, successBand)"),
            "success proof-state DIAG is per-page, includes Describe, and is emitted before any Footer.Exhausted-only line");
        if (failureLog < 0 || failureExhausted <= failureLog || successLog < 0 || successExhausted <= successLog) return;
        string failureFields = placement.Substring(failureLog, failureExhausted - failureLog);
        string successFields = placement.Substring(successLog, successExhausted - successLog);
        check(failureFields.Contains("nativeFooterVisible=") && failureFields.Contains("buttonMeasured=")
              && failureFields.Contains("active=") && failureFields.Contains("unmeasurable=")
              && failureFields.Contains("scanComplete=") && failureFields.Contains("parentCoverageComplete="),
            "failure proof diagnostic records footer visibility, button, active/unmeasurable counts and scan coverage");
        check(successFields.Contains("nativeFooterVisible=") && successFields.Contains("buttonMeasured=")
              && successFields.Contains("active=") && successFields.Contains("unmeasurable=")
              && successFields.Contains("scanComplete=") && successFields.Contains("parentCoverageComplete="),
            "success proof diagnostic records footer visibility, button, active/unmeasurable counts and scan coverage");
    }

    private static void RemeasureWordingNamesTheActualProbe(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the re-measure wording check locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        check(!display.Contains("both of this page's own edges")
              && display.Contains("current anchor-readability probe succeeded")
              && display.Contains("the success-page leave button is active and its rectangle is readable")
              && display.Contains("the failure-page strip is readable and its footer is measured or proven absent")
              && display.Contains("not the placement's measured-button result or a claim that both band edges were measured"),
            "success/failure recovery wording reports the probe it actually ran without claiming both edges or buttonMeasured");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    // The two patch bodies in the order the runtime runs them: `SeedBefore` only for a method the seed list accepts, then
    // the postfix's `Observe` for every observed call. One report, one credit - whichever declaring type it arrived on.
    private static double Call(HealthLossLedger ledger, string method, IntPtr seat, ulong owner,
        double before, double after, double maximum)
    {
        if (HealthLossTargets.Seedable(method)) ledger.Seed(seat, owner, before, maximum);
        return ledger.Observe(seat, owner, after, maximum);
    }

    private static int CountOf(string text, string token)
        => text == null || token == null ? 0 : text.Split(new[] { token }, StringSplitOptions.None).Length - 1;

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    // The text with its comment lines removed, so a comment may name a call this build must not make.
    private static string Code(string text)
    {
        var kept = new List<string>();
        if (text == null) return string.Empty;
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }

    // The body of the one method whose signature starts with `signature`, brace for brace.
    private static string MethodBody(string source, string signature)
    {
        if (source == null) return null;
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
}
