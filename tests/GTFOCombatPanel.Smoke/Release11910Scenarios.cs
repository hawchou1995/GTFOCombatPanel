using System;
using HawChow.GTFOCombatPanel;

// The four things 1.19.10 changes, each driven through the real policy it ships with.
//
//   A  the success board's *upper* bound: the completion section is measured as the pixels it really draws, so a
//      sector icon the page switched off and a padded container rectangle no longer decide where the table starts.
//      The failure page's board starts under its native title, and the wait for that title is bounded.
//   B  the tool cell: the figures a tool's ledger really recorded decide which block is printed, with the tool's
//      name only as a hint, and the row a slot is read from is the most specific real one instead of whichever
//      entry the dictionary yields first.
//   C  the blood-loss detour: `ReceiveParasiteDamage` is the one method left unpatched, and every other
//      `Receive*` of the damage base is still observed.
//   D  a hello that arrives before the roster holds its sender: it is re-judged against the refreshed roster,
//      through the same policy, and dropped once it is stale - the roster is still the only authority.
internal static class Release11910Scenarios
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

        CompletionUpperBound(check);
        FailureTitleBound(check);
        ScreenBoundsPolicy(check);
        UpperBoundPlacementPolicy(check);
        ToolDetailPolicy(check);
        WeaponSlotPolicy(check);
        BloodLossTargets(check);
        DeferredHelloPolicy(check);
    }

    // ---------------------------------------------------------------- A1: the completion section's own pixels
    private static void CompletionUpperBound(Action<bool, string> check)
    {
        // The real page: the sector box ends at 300, its header at 250, the icon of the sector the squad cleared
        // runs to 340 with its caption, and the second and third sector icons belong to sectors this run never
        // reached - the page switched those off, one more is present but faded out, and one field is null.
        var box = new CompletionPart(true, true, 1f, 300f, 300f);
        var header = new CompletionPart(true, true, 1f, 250f, 250f);
        var clearedIcon = new CompletionPart(true, true, 1f, 330f, 340f);
        var unclearedIcon = new CompletionPart(true, false, 1f, 900f, 900f);
        var fadedIcon = new CompletionPart(true, true, 0f, 800f, 800f);
        var missing = CompletionPart.Missing;

        float deepest = CompletionBounds.Deepest(new[] { box, header, unclearedIcon, fadedIcon, missing }, out int index);
        check(Near(deepest, 300f) && index == 0,
            $"a switched-off or faded-out optional sector icon contributes nothing: the bound is {deepest:0} from part {index}");
        check(index != 2 && index != 3, "the hidden optional icons are not the ones that produced the bound");
        check(!float.IsFinite(CompletionBounds.PartBottom(unclearedIcon))
              && !float.IsFinite(CompletionBounds.PartBottom(fadedIcon))
              && !float.IsFinite(CompletionBounds.PartBottom(missing)),
            "an inactive part, a part at zero alpha and an absent part each have no bottom edge at all");

        // A part that draws its own caption: the deepest visible renderer under it is what the player sees, and the
        // padded container rectangle - which ends well below the last caption - must not win over it.
        var padded = new CompletionPart(true, true, 1f, 520f, 400f);
        check(Near(CompletionBounds.PartBottom(padded), 400f),
            $"a padded container rectangle never outranks the renderers the part really draws: {CompletionBounds.PartBottom(padded):0}");
        float acrossParts = CompletionBounds.Deepest(new[] { box, header, clearedIcon }, out int clearedIndex);
        check(Near(acrossParts, 340f) && clearedIndex == 2,
            $"the deepest visible part - an icon plus its caption - sets the bound: {acrossParts:0} from part {clearedIndex}");

        // A part that draws nothing measurable still hands over its own rectangle: a page that reports geometry
        // without renderers is measured rather than ignored.
        var geometryOnly = new CompletionPart(true, true, 1f, 610f, float.NaN);
        check(Near(CompletionBounds.PartBottom(geometryOnly), 610f),
            "a part with no readable renderer falls back to its own container rectangle");

        // Nothing on the page could be measured: no bound and no source, which is the caller's fallback case.
        float none = CompletionBounds.Deepest(new[] { missing, unclearedIcon }, out int noneIndex);
        check(!float.IsFinite(none) && noneIndex == -1,
            "a completion section with nothing on screen produces no bound and no source");

        // ---- A2: the table hangs from the upper bound, and the button stays the strict lower bound -----------
        var strip = new BoardStrip(340f + 16f, 953f - 16f, true, 953f, 60f);
        bool placed = ResultBoardHold.TryPlace(1920, 1080, strip, 900f, 400f, 0f, out LayoutRect board, 2f, bottomAligned: false);
        check(placed && Near(board.Y, 356f, 0.01f),
            $"a board shorter than its strip starts on the strip's upper edge, not on the button: y={board.Y:0}");
        check(placed && board.Bottom <= 953f - ResultBoardHold.ButtonClearance,
            $"and it still clears the lobby button: board bottom {board.Bottom:0} against button top 953");
        check(placed && Near(board.Y, 356f, 0.01f),
            "the blank band the report was about belongs above the button now, not between the section and the table");

        // A board that cannot fit inside the strip is refused here, which is the only way this method can keep its
        // promise not to cover the button: the strip ends where the button begins.
        check(!ResultBoardHold.TryPlace(1920, 1080, strip, 900f, 601f, 0f, out _, 0f, bottomAligned: false),
            "a board taller than the strip is refused rather than drawn over the lobby button");

        // A board already up is lifted off the button rather than dropped, and never moved down onto it.
        bool held = ResultBoardHold.TryHold(strip, new LayoutRect(0f, 800f, 900f, 300f), 1080f, out LayoutRect lifted);
        check(held && lifted.Bottom <= strip.ButtonTop - ResultBoardHold.ButtonClearance,
            $"a held board that reaches the button is lifted onto the clearance line: bottom {lifted.Bottom:0}");
    }

    // ---------------------------------------------------------------- A3: the failure page's own title
    private static void FailureTitleBound(Action<bool, string> check)
    {
        check(FailureAlignment.Source(true, 380f, 1440f) == "mission-failed-text",
            "the native failure title is the only upper-bound source when it could be measured");
        check(FailureAlignment.Source(false, float.NaN, 1440f) == "none",
            "a failure page that has not measured its own title has no upper-anchor fallback");

        bool missingTitle = FailureAlignment.Resolve(false, float.NaN, 1440f, 0f, 16f, true, 953f,
            out _, out ResultsSafeArea missingStrip);
        check(!missingTitle && !missingStrip.Fits,
            "a title that cannot be measured is refused instead of borrowing a success edge or window fraction");
        bool missingFooter = FailureAlignment.Resolve(true, 380f, 1440f, 0f, 16f, false, float.NaN,
            out _, out _);
        check(!missingFooter, "a missing interaction lower wall cannot become a guessed footer bound");
        bool resolved = FailureAlignment.Resolve(true, 380f, 1440f, 0f, 16f, true, 953f,
            out float aligned, out ResultsSafeArea measuredStrip);
        check(resolved && Near(aligned, 380f) && Near(measuredStrip.Top, 396f),
            $"the measured title's own bottom edge plus the page gap starts the board: aligned={aligned:0}, top={measuredStrip.Top:0}");

        // The wait for that title is no longer a bounded give-up that ends in a placement: `ResultTitleGate` owns it
        // (full opacity over an edge that has stopped moving) and a page whose title never arrives keeps the game's own
        // screen. That rule is asserted in `Release11912Scenarios.FailureTitleNeedsFullOpacityOverASettledEdge`, and the
        // `WaitForTitle` helper this file used to pin is gone with it.
    }

    // ------------------------------------------- A4: the projected corners of a renderer, judged by one rule set
    //
    // The projection itself needs Unity and cannot run here. What *can* run here is the judgement every measurement
    // of a result page goes through, which is what `TryScreenRect` now hands its projected corners to: the eight
    // corners of a world-space `Renderer.bounds` and the four corners of a UI element's rect are accepted or refused
    // by exactly this code, so a world-space icon with no RectTransform is measured by the same rules as a label
    // that has one.
    private static void ScreenBoundsPolicy(Action<bool, string> check)
    {
        // A real 1920x1080 frame: the icon spans x 620..780, y 400..470 measured from the top.
        float[] xs = { 620f, 620f, 780f, 780f, 620f, 620f, 780f, 780f };
        float[] ys = { 400f, 470f, 400f, 470f, 400f, 470f, 400f, 470f };
        check(ScreenBounds.TryFromCorners(xs, ys, 1920f, 1080f, out LayoutRect icon)
              && Near(icon.X, 620f) && Near(icon.Y, 400f) && Near(icon.Width, 160f) && Near(icon.Height, 70f),
            $"a projected renderer box becomes one screen rectangle: {icon.X:0},{icon.Y:0} {icon.Width:0}x{icon.Height:0}");
        // Order of the corners is an accident of projection, so the box is their span and nothing else.
        float[] shuffledX = { 780f, 620f, 700f, 700f };
        float[] shuffledY = { 470f, 400f, 470f, 400f };
        check(ScreenBounds.TryFromCorners(shuffledX, shuffledY, 1920f, 1080f, out LayoutRect span)
              && Near(span.Width, 160f) && Near(span.Height, 70f),
            "the rectangle is the span of the corners, not the first and last one seen");

        // A corner that could not be projected makes the whole box unusable: half a box is not a box.
        float[] nanX = { 620f, float.NaN, 780f, 780f };
        float[] nanY = { 400f, 470f, 400f, 470f };
        check(!ScreenBounds.TryFromCorners(nanX, nanY, 1920f, 1080f, out _),
            "a non-finite projected corner refuses the whole measurement instead of shrinking the box");
        check(!ScreenBounds.TryFromCorners(new[] { 620f, 780f }, new[] { 400f }, 1920f, 1080f, out _)
              && !ScreenBounds.TryFromCorners(null, null, 1920f, 1080f, out _)
              && !ScreenBounds.TryFromCorners(new float[0], new float[0], 1920f, 1080f, out _),
            "a corner list that does not line up, and an empty one, measure nothing");

        // The rules that keep a bad reading out of the strip: a sliver, a box entirely off the frame, and a box that
        // is the whole window are not measurements of anything.
        check(!ScreenBounds.TryFromCorners(new[] { 620f, 620.5f }, new[] { 400f, 470f }, 1920f, 1080f, out _),
            "a rectangle a pixel wide is not a measurement");
        check(!ScreenBounds.TryFromCorners(new[] { 620f, 780f }, new[] { 400f, 400.5f }, 1920f, 1080f, out _),
            "and neither is one a pixel tall");
        check(!ScreenBounds.TryFromCorners(new[] { -300f, -200f }, new[] { 400f, 470f }, 1920f, 1080f, out _),
            "a box off the left of the frame measures nothing");
        check(!ScreenBounds.TryFromCorners(new[] { 620f, 780f }, new[] { 1200f, 1300f }, 1920f, 1080f, out _),
            "and neither does one below it");
        check(!ScreenBounds.TryFromCorners(new[] { -50f, 1970f }, new[] { -50f, 1130f }, 1920f, 1080f, out _),
            "a box that covers the whole window is not an element of the page");
        check(!ScreenBounds.TryFromCorners(xs, ys, 0f, 0f, out _),
            "a window with no size cannot be measured into");
        // A box that runs off one edge but is mostly on the frame is kept: the page's elements are allowed to be
        // clipped, and refusing them would drop a real icon for a real reason.
        check(ScreenBounds.TryFromCorners(new[] { -40f, 120f }, new[] { 400f, 470f }, 1920f, 1080f, out LayoutRect clipped)
              && Near(clipped.X, -40f) && Near(clipped.Width, 160f),
            "an element that is partly clipped by the frame is still measured");
    }

    // ------------------------------------------- A5: no placement without a measured upper bound
    private static void UpperBoundPlacementPolicy(Action<bool, string> check)
    {
        check(ResultsUpperBound.Decide(true, true) == ResultsUpperBound.Decision.Placed,
            "a measured completion section whose strip fits is placed");
        check(ResultsUpperBound.Decide(false, true) == ResultsUpperBound.Decision.Deferred,
            "an unmeasurable completion section is deferred, never placed against a fraction of the window");
        check(ResultsUpperBound.Decide(false, false) == ResultsUpperBound.Decision.Deferred,
            "and so is a page that measured nothing at all");
        check(ResultsUpperBound.Decide(true, false) == ResultsUpperBound.Decision.Deferred,
            "a measured section whose strip cannot hold the table is refused rather than squeezed over an element");
        // The dangerous constant is still what `Resolve` documents, but the decision above is the only thing the
        // success page may act on: that is the property this pair asserts, and it holds no matter what the fraction is.
        check(ResultsUpperBound.Decide(true, ResultsRegion.Resolve(true, 410f, true, 953f, 1080f, 16f).Fits)
              == ResultsUpperBound.Decision.Placed,
            "the real strip of a real success page is placed");
        check(ResultsUpperBound.Decide(false, ResultsRegion.Resolve(false, 0f, true, 953f, 1080f, 16f).Fits)
              == ResultsUpperBound.Decision.Deferred,
            "while the same page without a measured section is deferred even though Resolve still answers with a region");
    }

    // ---------------------------------------------------------------- B1: which tool block a cell prints
    private static void ToolDetailPolicy(Action<bool, string> check)
    {
        var sentry = new ImpactCounters { SentryShots = 12, SentryHits = 9 };
        sentry.AddSentryDamage(1234.5);
        check(ImpactBoard.ToolDetailOf(sentry, "") == ImpactBoard.ToolDetailKind.Sentry,
            "a sentry's own recorded figures print the sentry block even when the row carries no name at all");
        check(ImpactBoard.ToolDetailOf(sentry, "自动炮台") == ImpactBoard.ToolDetailKind.Sentry,
            "and when the row is called by a Chinese name this build never listed");
        check(ImpactBoard.ToolDetailOf(sentry, "GearClass") == ImpactBoard.ToolDetailKind.Sentry,
            "and when the row still carries the slot placeholder as its name");
        check(ImpactBoard.ToolDetailOf(new ImpactCounters(), "Sentry Gun") == ImpactBoard.ToolDetailKind.Sentry
              && ImpactBoard.ToolDetailOf(new ImpactCounters(), "哨戒炮") == ImpactBoard.ToolDetailKind.Sentry,
            "a sentry that has not fired yet is still read as a sentry from its name, in either language");
        check(ImpactBoard.ToolDetailOf(new ImpactCounters(), "") == ImpactBoard.ToolDetailKind.None
              && ImpactBoard.ToolDetailOf(null, null) == ImpactBoard.ToolDetailKind.None,
            "no figures and no name is no block at all, rather than a sentry row of zeros");

        var mine = new ImpactCounters { MineDeployments = 2 };
        check(ImpactBoard.ToolDetailOf(mine, "Sentry Gun") == ImpactBoard.ToolDetailKind.Mine,
            "figures win over a wrong name: a mine that reported deployments is printed as a mine");
        var glue = new ImpactCounters { GlueShots = 4, GlueHits = 3 };
        glue.CreditGluedEnemy();
        check(ImpactBoard.ToolDetailOf(glue, "GearClass") == ImpactBoard.ToolDetailKind.Glue,
            "the same for a glue gun whose row was never named");

        // The cell itself, through the real formatter and the real counter set.
        var entry = new ImpactEntry(new ImpactInput { ToolName = "", Counters = sentry }, null, null, 50, 1,
            ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        string cell = ImpactBoard.ToolEfficiencyText(entry);
        check(cell.Contains("12") && cell.Contains("9") && !string.Equals(cell, CombatStrings.Placeholder),
            $"an unnamed sentry row prints its shots, hits and damage instead of the placeholder: \"{cell}\"");
        var unnamed = new ImpactEntry(new ImpactInput { ToolName = "", Counters = new ImpactCounters() }, null, null, 50, 1,
            ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        check(string.Equals(ImpactBoard.ToolEfficiencyText(unnamed), CombatStrings.Placeholder),
            "a tool with nothing recorded and no name is the placeholder, unchanged from before this release");
    }

    // ---------------------------------------------------------------- B2: which row a slot is read from
    private static void WeaponSlotPolicy(Action<bool, string> check)
    {
        // The reported shape: the slot's placeholder row is opened first, and the real row joins the same slot
        // later under an archetype id of its own - two different rows of one slot.
        var player = new CombatPlayer(0x44UL);
        var placeholder = player.Weapon(4, 0, "GearClass");
        var real = player.Weapon(4, 777, "Sentry", 9001, true);
        check(ReferenceEquals(WeaponRows.For(player.Weapons, 4), real),
            "the real row of a duplicated slot wins over the placeholder the slot was opened with");
        check(WeaponRows.Rank(real) > WeaponRows.Rank(placeholder),
            "and it ranks above it: an archetype id and a real name against neither");

        // The other insertion order answers the same, and so does a row that is only named.
        var other = new CombatPlayer(0x45UL);
        var realFirst = other.Weapon(4, 4242, "Sentry", 9001, true);
        other.Weapon(4, 0, "GearClass");
        check(ReferenceEquals(WeaponRows.For(other.Weapons, 4), realFirst),
            "the row with an archetype id of its own wins whatever the insertion order was");
        var namedOnly = new CombatPlayer(0x47UL);
        var named = namedOnly.Weapon(4, 0, "Sentry", 9001, true);
        namedOnly.Weapon(4, 0, "GearClass");
        check(ReferenceEquals(WeaponRows.For(namedOnly.Weapons, 4), named),
            "a row a real source named wins over the placeholder even without an archetype id");

        var onlyPlaceholder = new CombatPlayer(0x46UL);
        var lone = onlyPlaceholder.Weapon(4, 0, "GearClass");
        check(ReferenceEquals(WeaponRows.For(onlyPlaceholder.Weapons, 4), lone),
            "a slot that only ever held its placeholder is still answered with it rather than with nothing");
        check(WeaponRows.For(onlyPlaceholder.Weapons, 1) == null && WeaponRows.For(null, 4) == null,
            "a slot with no row, and a player with no rows, resolve to nothing");
    }

    // ---------------------------------------------------------------- C: the one method left unpatched
    private static void BloodLossTargets(Action<bool, string> check)
    {
        check(!HealthLossTargets.Accepts(HealthLossTargets.ParasiteDamage)
              && string.Equals(HealthLossTargets.ParasiteDamage, "ReceiveParasiteDamage", StringComparison.Ordinal),
            "ReceiveParasiteDamage is the one damage-base method this mod does not detour");
        string[] keptReceive =
        {
            "ReceiveBulletDamage", "ReceiveMeleeDamage", "ReceiveFireDamage",
            "ReceiveExplosionDamage", "ReceivePushDamage", "ReceiveSomeFutureDamageKind"
        };
        foreach (string kept in keptReceive)
            check(HealthLossTargets.Accepts(kept), $"every other Receive* of the damage base is still observed: {kept}");
        string[] keptEvents = { "Setup", "OnRevive", "OnApplyDamageOverTimeModifier", "OnKilledByTankTentacle" };
        foreach (string kept in keptEvents)
            check(HealthLossTargets.Accepts(kept), $"the named events the ledger seeds and re-bases on are still observed: {kept}");
        check(!HealthLossTargets.Accepts("BlinkIn") && !HealthLossTargets.Accepts("") && !HealthLossTargets.Accepts(null),
            "an unrelated method name is not swept into the pattern, and neither is an empty one");
        // The mitigation is about exactly one name: the exclusion is an exact string, so a distinct method that
        // merely starts alike still reports, and a second exception has to be added to this class and its test.
        check(HealthLossTargets.Accepts("ReceiveParasiteDamageExtra"),
            "the exclusion is an exact name, not a prefix - a distinct method that merely starts alike still reports");
    }

    // ---------------------------------------------------------------- D: a hello that beat the roster
    private static void DeferredHelloPolicy(Action<bool, string> check)
    {
        const ulong local = 0x51UL, host = 0x52UL, stranger = 0x53UL;
        var exchange = new SquadExchange();
        exchange.Configure(local, 0, 1, 2, 3);
        var localPeer = new SquadPeer(local, 0, true, false, false);
        var hostPeer = new SquadPeer(host, 1, false, false, true);

        // The frame this release fixes: the roster does not hold the host yet, so the hello is refused - and kept.
        SquadDecision refused = exchange.OnHello(host, 1, 0f, out bool answered, true);
        check(!refused.Accepted && !answered && exchange.DeferredHellos == 1,
            "a hello that arrives before the roster holds its sender is refused and kept for the roster refresh");

        // The roster is refreshed in the same tick, and the packet is re-judged against it by the same policy.
        exchange.SetRoster(new[] { localPeer, hostPeer }, false);
        bool taken = exchange.TryTakeDeferredHello(0f, out ulong sender, out uint generation);
        check(taken && sender == host && generation == 1,
            "the kept hello is handed back on the frame the roster knows its sender");
        SquadDecision accepted = exchange.OnHello(sender, generation, 0f, out bool replied, false);
        check(accepted.Accepted && replied,
            "and it is accepted and answered on that frame instead of on the peer's next two-second cadence");
        check(exchange.DeferredHellos == 0, "the retried hello is not parked a second time");

        // A sender that is still outside the squad when the packet is re-judged is refused again, not accepted.
        SquadDecision refusedStranger = exchange.OnHello(stranger, 1, 1f, out _, true);
        check(!refusedStranger.Accepted && exchange.DeferredHellos == 1,
            "a hello from a player who is not in the squad is refused and kept only for the bounded retry");
        exchange.TryTakeDeferredHello(1f, out ulong keptSender, out uint keptGeneration);
        SquadDecision stillOutside = exchange.OnHello(keptSender, keptGeneration, 1f, out _, false);
        check(!stillOutside.Accepted && stillOutside.Reason != null && stillOutside.Reason.Contains("roster"),
            $"re-judging it against a roster that still does not hold it refuses it again: {stillOutside.Reason}");
        check(exchange.DeferredHellos == 0, "and the second refusal ends the matter rather than parking it again");

        // The retry window is bounded, so a packet that waits too long is dropped instead of being re-judged.
        exchange.OnHello(stranger, 1, 10f, out _, true);
        bool staleTaken = exchange.TryTakeDeferredHello(10f + SquadExchange.DeferredHelloSeconds + 0.5f, out _, out _);
        check(!staleTaken && exchange.DeferredHellos == 0,
            "a hello older than the retry window is dropped rather than re-judged against a roster that moved on");

        // The list is bounded as well: a lobby where every peer greets early cannot grow it without limit.
        for (ulong peer = 0x60UL; peer < 0x60UL + 20UL; peer++) exchange.OnHello(peer, 1, 20f, out _, true);
        check(exchange.DeferredHellos <= SquadExchange.MaxDeferredHellos,
            $"the deferred list is bounded: {exchange.DeferredHellos} entries");
        int drained = 0;
        while (drained < 64 && exchange.TryTakeDeferredHello(20f, out _, out _)) drained++;
        check(drained <= SquadExchange.MaxDeferredHellos && exchange.DeferredHellos == 0,
            "and every entry is taken at most once");

        // A hello kept from the run that just ended must not survive the reset: the roster of the new expedition is
        // rebuilt from nothing, so re-judging an old packet against it would answer a machine for a session it is not
        // in. `CombatSync.ResetExpedition` is the explicit path this asserts (`SquadExchange.ResetForExpedition`).
        var reset = new SquadExchange();
        reset.Configure(local, 0, 1, 2, 3);
        reset.SetRoster(new[] { localPeer }, false);
        reset.OnHello(stranger, 1, 5f, out _, true);
        check(reset.DeferredHellos == 1, "a hello is waiting before the expedition ends");
        reset.ResetForExpedition();
        check(reset.DeferredHellos == 0, "the expedition reset drops the hellos kept from the run that ended");
        reset.SetRoster(new[] { localPeer, hostPeer, new SquadPeer(stranger, 2, false, false, false) }, false);
        check(!reset.TryTakeDeferredHello(5f, out _, out _) && reset.DeferredHellos == 0,
            "and a roster that now holds that peer cannot resurrect the old packet into the new expedition");
        // The reset does not touch the trust gate: a fresh hello from the same peer is judged by the same policy,
        // and one from a peer outside the new roster is refused again.
        check(reset.OnHello(stranger, 1, 6f, out _, false).Accepted,
            "a hello that arrives after the reset from a player in the new roster is accepted, as before");
        check(!reset.OnHello(0x55UL, 1, 7f, out _, false).Accepted
              && reset.DeferredHellos == 0,
            "and one from a player outside the new roster is refused without being kept");

        // The trust gate itself is untouched: a row from a peer outside the roster is still refused, deferred hello
        // or not, and a row from a member that is not the host may not describe somebody else.
        var gated = new SquadExchange();
        gated.Configure(local, 0, 1, 2, 3);
        gated.SetRoster(new[] { localPeer, hostPeer }, false);
        var strangerRow = new CombatPlayer(stranger) { Name = "Stranger" };
        strangerRow.Total.AddEnemyDamage(100);
        check(!gated.OnSnapshot(stranger, 1, SyncProtocol.Capture(strangerRow, 0, 1, 2, 3, 1000, false), 1f).Accepted,
            "a row from a peer outside the roster is refused, exactly as before this release");
        var clientPeer = new SquadPeer(0x54UL, 2, false, false, false);
        gated.SetRoster(new[] { localPeer, hostPeer, clientPeer }, false);
        var hostRow = new CombatPlayer(host) { Name = "Host" };
        hostRow.Total.AddEnemyDamage(50);
        check(!gated.OnSnapshot(0x54UL, 1, SyncProtocol.Capture(hostRow, 0, 1, 2, 3, 1000, false), 1f).Accepted,
            "and a row about another player from a peer that is not the host is still refused");
        check(gated.OnSnapshot(host, 1, SyncProtocol.Capture(hostRow, 0, 1, 2, 3, 1000, true), 1f).Accepted,
            "while the host's own row is accepted, so the retry did not weaken authority");
    }

    private static bool Near(float a, float b) => Near(a, b, 0.001f);
    private static bool Near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;
}
