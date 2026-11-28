[CmdletBinding()]
param(
    [string]$DotnetExe = 'dotnet',
    [string]$GTFO_DIR = $env:GTFO_DIR,
    [string]$BuildOutputDirectory,
    [switch]$DisableEwcBridge
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GTFO_DIR)) {
    $GTFO_DIR = 'C:\Program Files (x86)\Steam\steamapps\common\GTFO'
}
$GTFO_DIR = (Resolve-Path -LiteralPath $GTFO_DIR).Path
$env:GTFO_DIR = $GTFO_DIR
    $pluginProject = Join-Path $projectRoot 'GTFOCombatPanel.csproj'
    $testProject = Join-Path $projectRoot 'tests\GTFOCombatPanel.Smoke\GTFOCombatPanel.Smoke.csproj'
    if ([string]::IsNullOrWhiteSpace($BuildOutputDirectory)) { $BuildOutputDirectory = Join-Path $projectRoot 'bin\Release' }
    $BuildOutputDirectory = [IO.Path]::GetFullPath($BuildOutputDirectory)
    [IO.Directory]::CreateDirectory($BuildOutputDirectory) | Out-Null
    $pluginDll = Join-Path $BuildOutputDirectory 'GTFOCombatPanel.dll'
    $manifestPath = Join-Path $projectRoot 'Thunderstore\manifest.json'

foreach ($required in @(
    (Join-Path $GTFO_DIR 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'),
    (Join-Path $GTFO_DIR 'BepInEx\interop\Modules-ASM.dll'),
    (Join-Path $GTFO_DIR 'BepInEx\interop\SNet_ASM.dll')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required local GTFO assembly is missing: $required" }
}

Push-Location $projectRoot
try {
    Write-Host "GTFO_DIR=$GTFO_DIR"
    $profileEwcDirectory = Join-Path $GTFO_DIR 'BepInEx\plugins\Dinorush-ExtraWeaponCustomization'
    $profileEwcDll = Join-Path $profileEwcDirectory 'ExtraWeaponCustomization.dll'
    if ($DisableEwcBridge) {
        $ewcDirectory = Join-Path $env:PI_SCRATCH_DIR ('ewc-disabled-' + [guid]::NewGuid().ToString('N'))
    }
    else {
        $ewcDirectory = $env:EWC_DIR
        if ([string]::IsNullOrWhiteSpace($ewcDirectory)) { $ewcDirectory = $profileEwcDirectory }
    }
    $selectedEwcDll = Join-Path $ewcDirectory 'ExtraWeaponCustomization.dll'
    if (-not $DisableEwcBridge -and (Test-Path -LiteralPath $profileEwcDll -PathType Leaf) -and -not (Test-Path -LiteralPath $selectedEwcDll -PathType Leaf)) {
        throw "EWC_DIR '$ewcDirectory' misses the EWC DLL installed beside the selected GTFO profile."
    }
    $env:EWC_DIR = $ewcDirectory
    Write-Host "EWC_DIR=$ewcDirectory; EWC_PRESENT=$(Test-Path -LiteralPath $selectedEwcDll); BRIDGE_DISABLED=$DisableEwcBridge"
    Write-Host 'Building against the installed GTFO BepInEx and Interop assemblies...'
    $outputPath = $BuildOutputDirectory.TrimEnd('\') + '\'
    & $DotnetExe build $pluginProject --configuration Release --verbosity minimal "-p:GTFO_DIR=$GTFO_DIR" "-p:EWC_DIR=$ewcDirectory" "-p:OutputPath=$outputPath"
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $pluginDll -PathType Leaf)) { throw "Build did not produce $pluginDll" }
    & (Join-Path $PSScriptRoot 'Test-ScoutHookSafety.ps1') -PluginDll $pluginDll -CecilDll (Join-Path $GTFO_DIR 'BepInEx\core\Mono.Cecil.dll')
    $pluginReferences = @([Reflection.Assembly]::LoadFrom($pluginDll).GetReferencedAssemblies() | ForEach-Object { $_.Name })
    # The public DLL must never carry an ExtraWeaponCustomization reference: it has to run on a profile
    # that does not have EWC at all. The projectile bridge is always compiled in instead, and binds EWC's
    # public events by reflection at runtime - so dropping the reference must not drop the capability.
    if ($pluginReferences -contains 'ExtraWeaponCustomization') { throw 'The public plugin must not reference ExtraWeaponCustomization.' }
    $publicAssembly = [Reflection.Assembly]::LoadFrom($pluginDll)
    $bridgeType = $publicAssembly.GetType('HawChow.GTFOCombatPanel.EwcProjectileBridge', $false)
    if ($null -eq $bridgeType) { throw 'The dependency-free EWC projectile bridge must be compiled into every build.' }
    Write-Host 'OPTIONAL_EWC_BRIDGE: PASS (no assembly reference; runtime event binding compiled in)'
    if ($DisableEwcBridge) { Write-Host 'EWC_API_CONTRACT: skipped by -DisableEwcBridge' }
    elseif (Test-Path -LiteralPath $selectedEwcDll -PathType Leaf) {
        & (Join-Path $PSScriptRoot 'Test-EwcApi.ps1') -EwcDll $selectedEwcDll -CecilDll (Join-Path $GTFO_DIR 'BepInEx\core\Mono.Cecil.dll')
        if ($LASTEXITCODE -ne 0) { throw "The EWC DLL beside this profile does not expose the members the bridge reads." }
    }
    else { Write-Host 'EWC_API_CONTRACT: skipped; this profile has no EWC DLL to check against.' }
    Write-Host 'Running combat statistics and formatting smoke tests...'
    & $DotnetExe run --project $testProject --configuration Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed with exit code $LASTEXITCODE" }
    Write-Host 'Running production config file watcher tests...'
    & $DotnetExe run --project (Join-Path $projectRoot 'tests\GTFOCombatPanel.ConfigWatcher\GTFOCombatPanel.ConfigWatcher.csproj') --configuration Release --verbosity minimal "-p:GTFO_DIR=$GTFO_DIR"
    if ($LASTEXITCODE -ne 0) { throw "Config watcher tests failed with exit code $LASTEXITCODE" }
    Write-Host 'Running supply giver attribution tests...'
    & $DotnetExe run --project (Join-Path $projectRoot 'tests\GTFOCombatPanel.Supply\GTFOCombatPanel.Supply.csproj') --configuration Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Supply attribution tests failed with exit code $LASTEXITCODE" }
    Write-Host 'Running native scan request/confirmation pipeline replay...'
    & $DotnetExe run --project (Join-Path $projectRoot 'tests\GTFOCombatPanel.Scan\GTFOCombatPanel.Scan.csproj') --configuration Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Scan pipeline tests failed with exit code $LASTEXITCODE" }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    # The expected version is read from the plugin itself, so this check cannot go stale the next time the
    # version is bumped: the package and the DLL are compared against the one place the version is written.
    $pluginInfo = Get-Content -LiteralPath (Join-Path $projectRoot 'PluginInfo.cs') -Raw
    $pluginVersion = [regex]::Match($pluginInfo, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
    if ($manifest.name -ne 'CombatPanel' -or $manifest.version_number -ne $pluginVersion) {
        throw "Thunderstore package name/version mismatch: the manifest says '$($manifest.version_number)', the plugin says '$pluginVersion'."
    }
    if ($manifest.dependencies.Count -ne 1 -or $manifest.dependencies[0] -ne 'BepInEx-BepInExPack_GTFO-3.2.4') {
        throw 'Package must depend on the BepInEx GTFO pack only.'
    }
    if ($manifest.description.Length -gt 256) {
        throw "Thunderstore rejects a manifest description longer than 256 characters; this one is $($manifest.description.Length)."
    }
    $hudProjection = (Get-Content (Join-Path $projectRoot 'PanelBehaviour.cs') -Raw).Contains('CombatSync.DisplayRows().OrderBy(')
    $resultProjection = (Get-Content (Join-Path $projectRoot 'ResultsDisplay.cs') -Raw).Contains('CombatTracker.SettlementView().OrderBy(')
    $handoff = (Get-Content (Join-Path $projectRoot 'CombatTracker.cs') -Raw).Contains('ResultRows.Update(CombatSync.DisplayRows(), CombatSync.Generation)')
    if (-not $hudProjection -or -not $resultProjection -or -not $handoff) {
        throw 'HUD and settlement handoff must consume the shared squad projection.'
    }
    Write-Host 'DISPLAY_PROJECTION_WIRING: PASS'
    $resultsSource = Get-Content (Join-Path $projectRoot 'ResultsDisplay.cs') -Raw
    $impactSource = Get-Content (Join-Path $projectRoot 'ImpactMetrics.cs') -Raw
    $hasToolSlot = $resultsSource.Contains('WeaponOf(player, InventorySlot.GearClass)')
    $passesToolName = $resultsSource.Contains('                tool);')
    $mapsToolName = $impactSource.Contains('ToolName = tool?.Name ?? string.Empty')
    if (-not $hasToolSlot -or -not $passesToolName -or -not $mapsToolName) {
        throw 'Settlement tool rows must carry the equipped GearClass identity into ImpactInput.'
    }
    Write-Host 'SETTLEMENT_TOOL_PROJECTION: PASS'
    # The real completion edge still protects the success page even though the board is bottom-aligned.
    $usesVisibleCompletion = $resultsSource.Contains('CompletionBounds.Deepest(') -and $resultsSource.Contains('Completion.Observe(Time.frameCount, true, area.Top)')
    $anchorsOnLowerEdge = $resultsSource.Contains('bottomAligned: true') -and -not $resultsSource.Contains('bottomAligned: false')
    # Failure logs the title used for readiness separately from the actual placement edge.
    $namesTheAnchor = $resultsSource.Contains('titleSource={titleSource}') -and $resultsSource.Contains('anchorSource=')
    # The failure page's own title is now gated by `ResultTitleGate` (full opacity over a still edge) and there is no
    # bounded give-up that ends in a placement: the strip is refused until the gate answers settled.
    $refusesUnsettledTitle = $resultsSource.Contains('anchorSettled = Title.Observe(Time.frameCount, active, alpha, measured') -and $resultsSource.Contains('if (!anchorSettled)')
    # A world-space renderer is measured from its own bounds, so the projection must go through `ScreenBounds` and
    # must not sit behind a RectTransform any more; and a success page whose completion section could not be measured
    # is deferred (`ResultsUpperBound.Decide`) instead of being placed at the historical fraction of the window,
    # which sits above the icons and captions. The smoke suite asserts both policies; these two lines assert that the
    # runtime actually calls them.
    $measuresRendererBounds = $resultsSource.Contains('ScreenBounds.TryFromCorners(') -and $resultsSource.Contains('renderer.bounds')
    $defersWithoutMeasuredSection = $resultsSource.Contains('ResultsUpperBound.Decide(') -and -not $resultsSource.Contains('ResultsRegion.Resolve(false')
    $upperBoundBroken = (-not $usesVisibleCompletion) -or (-not $anchorsOnLowerEdge) -or (-not $namesTheAnchor) -or (-not $refusesUnsettledTitle) -or (-not $measuresRendererBounds) -or (-not $defersWithoutMeasuredSection)
    if ($upperBoundBroken) {
        throw 'The result board must preserve the measured completion section, bottom-align inside the safe strip, defer if the completion edge is unreadable, identify its anchors, and keep the failure-page readiness gate.'
    }
    Write-Host 'RESULT_BOARD_UPPER_BOUND: PASS'
    # Neither settlement page may be measured before its own anchors are real, and no number of frames may turn a
    # missing anchor into a placement: the gates only answer for an edge that was really read twice, the title only for
    # full opacity over a still edge, and a page that never hands one over keeps the vanilla screen and is retried at the
    # bounded cadence. A page that moves after the board was placed is fitted again without an F8 toggle. The policies
    # themselves are asserted in tests/GTFOCombatPanel.Smoke/Release11912Scenarios.cs; these lines assert the runtime
    # calls them.
    $waitsForSettledTitle = $resultsSource.Contains('anchorSettled = Title.Observe(Time.frameCount, active, alpha, measured') -and $resultsSource.Contains('if (!anchorSettled)')
    $waitsForSettledFooter = $resultsSource.Contains('Footer.Observe(Time.frameCount, measured, top)')
    $waitsForSettledUpperBound = $resultsSource.Contains('Completion.Observe(Time.frameCount, true, area.Top)')
    $boundedRetry = $resultsSource.Contains('if (!Retry.Due(Time.frameCount)) return;')
    $noTimeoutReady = -not (Get-Content (Join-Path $projectRoot 'ResultsPagePolicy.cs') -Raw).Contains('|| _attempts >= SettleAttempts')
    $anchorLatchedAsGuess = $resultsSource.Contains('strip.ButtonMeasured && strip.AnchorMeasured && float.IsFinite(strip.ButtonHeight)')
    $remeasuresMovedPage = $resultsSource.Contains('Drift.Record(Time.frameCount, strip.Top, strip.Bottom - strip.Top);') -and $resultsSource.Contains('AnchorDriftDue(page, succeeded)')
    $settleBroken = (-not $waitsForSettledTitle) -or (-not $waitsForSettledFooter) -or (-not $waitsForSettledUpperBound) -or (-not $boundedRetry) -or (-not $noTimeoutReady) -or (-not $anchorLatchedAsGuess) -or (-not $remeasuresMovedPage)
    if ($settleBroken) {
        throw 'Both settlement pages must place only from a real settled anchor, sample each Unity frame once, retry missing anchors at a bounded cadence, and re-fit a moved page without an F8 toggle.'
    }
    Write-Host 'RESULT_PAGE_SETTLE: PASS'
    $policySource = Get-Content (Join-Path $projectRoot 'ResultsPagePolicy.cs') -Raw
    $failureAnchorPolicy = $policySource.Contains('public static class FailureTitleAnchorPolicy') -and $policySource.Contains('bool missionActive, bool missionTextPresent, bool missionGeometryMeasured, bool failureSequenceFinished)') -and $policySource.Contains('FailureTitleAnchorKind.MissionTitleLayoutAfterSequence') -and $resultsSource.Contains('FailureTitleAnchorPolicy.Select(client, nativeFooterVisible') -and $resultsSource.Contains('TextMeshPro heading = missionTitle;') -and $resultsSource.Contains('protectWaitingStatus ? guestStatus : null') -and $resultsSource.Contains('FailureSequenceStartedPatch') -and $resultsSource.Contains('FailureSequenceFinishedPatch') -and $resultsSource.Contains('failureSequenceFinished={sequenceFinished.ToString().ToLowerInvariant()}') -and $resultsSource.Contains('titleSource={titleSource}') -and $resultsSource.Contains('sameFrameInvalidations={Title.SameFrameInvalidations}') -and $resultsSource.Contains('geometryMeasured={measured.ToString().ToLowerInvariant()}') -and $resultsSource.Contains('rawTextAlpha={(float.IsFinite(rawTextAlpha)') -and $resultsSource.Contains('if (!float.IsFinite(text)) return float.NaN;') -and $resultsSource.Contains('if (!float.IsFinite(groupAlpha)) return float.NaN;') -and -not $resultsSource.Contains('FailureTitleAnchorKind.GuestWaitingStatus')
    if (-not $failureAnchorPolicy) { throw 'Failure title placement must use a full-opacity native title or a measured mission-title layout after native sequence completion; waiting status is lower protection only.' }
    Write-Host 'RESULT_FAILURE_TITLE_ANCHOR: PASS'
    $siblingParentCoverage = $resultsSource.Contains('Transform parent = source.transform.parent;') -and $resultsSource.Contains('parent.GetComponentsInChildren<CM_Item>(true)') -and $resultsSource.Contains('parentCoverageComplete')
    $nativeControlsMeasured = $resultsSource.Contains('menuPage.m_allItems') -and $resultsSource.Contains('GetComponentsInChildren<CM_Item>(true)') -and $siblingParentCoverage
    $noFooterNeedsProof = $policySource.Contains('public sealed class ResultNoFooterGate') -and $resultsSource.Contains('ResultNoFooterSafeArea.TryResolve') -and $resultsSource.Contains('controls.FooterTop')
    $pageNativeFlags = $resultsSource.Contains('success.m_lobbyButtonVisible') -and $resultsSource.Contains('nativeFooterVisible = fail.m_lobbyButtonVisible')
    $measuredSafeAreaSource = $resultsSource.Contains('ResultFooterSource.ClientNoFooterSafeArea') -and $resultsSource.Contains('if (!BoardStillSafe())') -and $resultsSource.Contains('ResultInteractionStripPolicy.OverlapsBoard(') -and $resultsSource.Contains('DeferBoardForInteraction();')
    if (-not $nativeControlsMeasured -or -not $noFooterNeedsProof -or -not $pageNativeFlags -or -not $measuredSafeAreaSource) {
        throw 'Guest safe-area placement requires page registrations/tree, local action-parent scans, page visibility flags, a bounded proof gate and per-draw collision rejection.'
    }
    Write-Host 'RESULT_CLIENT_NO_FOOTER_SAFE_AREA: PASS'
    # The tool cell is chosen from the recorded figures first (the name is only a hint), and a slot is read from
    # its most specific real row rather than from the first entry the dictionary happens to yield.
    $toolDetailFromData = (Get-Content (Join-Path $projectRoot 'ImpactBoard.cs') -Raw).Contains('ToolDetailOf(counters, name)')
    $slotRowPolicy = $resultsSource.Contains('WeaponRows.For(player.Weapons, (int)slot)')
    if (-not $toolDetailFromData -or -not $slotRowPolicy) {
        throw 'The tool cell and the tool slot row must both be decided by recorded figures and the most specific real row.'
    }
    Write-Host 'SETTLEMENT_TOOL_DATA_FIRST: PASS'
    # The parasite-damage crash mitigation is one exclusion and nothing else: `HealthLossTargets.Accepts` is the only
    # observation list, it leaves `ReceiveParasiteDamage` out and names no other method, and the host gate on the sentry
    # ledger is untouched. 1.19.33 adds exactly one prefix and 1.19.35 restricts what it may seed; neither moves the credit.
    # The prefix calls the one seed entry point, that entry point fills the watermark from the game's own pre-call health,
    # and the *only* place `HealthLost` grows is the postfix, behind the level gate. The seed list
    # (`HealthLossTargets.Seedable`) is the accepted calls that report a hit, which is why `ReceiveTentacleGrabStop` is
    # named there: it carries damage and is therefore seeded - it is not a second exclusion, and `Accepts` still observes it.
    $healthSource = Get-Content (Join-Path $projectRoot 'HealthLossPatches.cs') -Raw
    $targetsSource = Get-Content (Join-Path $projectRoot 'HealthLossLedger.cs') -Raw
    # Comments are stripped before any rule is read: both files document the names they exclude, and a rule read from a
    # comment is not a rule.
    $healthCode = (($healthSource -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n"
    $targetsCode = (($targetsSource -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n"
    $acceptsBody = ([regex]::Match($targetsCode, 'public static bool Accepts\(string name\)\s*\{(?<body>.*?)\n    \}', 'Singleline')).Groups['body'].Value
    $seedableBody = ([regex]::Match($targetsCode, 'public static bool Seedable\(string name\)\s*\{(?<body>.*?)\n    \}', 'Singleline')).Groups['body'].Value
    $seedBody = ([regex]::Match($healthCode, 'internal static void SeedBefore\(Dam_PlayerDamageBase instance\)\s*\{(?<body>.*?)\n    \}', 'Singleline')).Groups['body'].Value
    $usesTargetPolicy = $healthCode.Contains('DamageBaseMethods(HealthLossTargets.Accepts)') -and `
        $healthCode.Contains('HealthLossPatches.DamageBaseMethods(HealthLossTargets.Seedable)')
    $oneExclusion = $acceptsBody.Contains('if (name == ParasiteDamage) return false;') -and `
        ([regex]::Matches($acceptsBody, 'return false;')).Count -eq 2 -and `
        ([regex]::Matches($acceptsBody, 'Receive')).Count -eq 1 -and `
        $targetsCode.Contains('public const string ParasiteDamage = "ReceiveParasiteDamage";') -and `
        ([regex]::Matches($targetsCode, '"Receive[A-Za-z]+"')).Count -eq 2 -and `
        $seedableBody.Contains('if (!Accepts(name)) return false;') -and `
        $seedableBody.Contains('if (name.EndsWith("Damage", StringComparison.Ordinal)) return true;') -and `
        $seedableBody.Contains('return name == "ReceiveTentacleGrabStop";')
    # Both slices are taken from the stripped text as well: a prefix body read out of a commented-out call would make
    # `Contains` below true with no call in the code, which is exactly the regression this rule has to catch.
    $prefixBody = (($healthCode -split '\[HarmonyPrefix\]')[1] -split '\[HarmonyPostfix\]')[0]
    $oneSeedPrefix = ([regex]::Matches($healthSource, '\[HarmonyPrefix\]')).Count -eq 1 -and `
        ([regex]::Matches($healthSource, '\[HarmonyPostfix\]')).Count -eq 1 -and `
        $prefixBody.Contains('HealthLossPatches.SeedBefore(__instance)') -and `
        $seedBody.Contains('Ledger.Seed(instance.Pointer, lookup, instance.Health, instance.HealthMax);') -and `
        ([regex]::Matches($healthCode, 'Ledger\.Seed\(')).Count -eq 1 -and `
        ([regex]::Matches($healthCode, 'Ledger\.Observe\(')).Count -eq 1
    $seedCreditsNothing = $prefixBody.Length -gt 0 -and -not $prefixBody.Contains('HealthLost') -and -not $prefixBody.Contains('InLevel') -and `
        $seedBody.Length -gt 0 -and -not $seedBody.Contains('HealthLost') -and -not $seedBody.Contains('InLevel') -and `
        ([regex]::Matches($healthCode, 'HealthLost = Math\.Min')).Count -eq 1 -and `
        $healthCode.Contains('GameStateManager.CurrentStateName != eGameStateName.InLevel')
    $observed = $healthCode.IndexOf('Ledger.Observe(__instance.Pointer', [StringComparison]::Ordinal)
    $gate = $healthCode.IndexOf('GameStateManager.CurrentStateName != eGameStateName.InLevel', [StringComparison]::Ordinal)
    $credit = $healthCode.IndexOf('counters.HealthLost = Math.Min', [StringComparison]::Ordinal)
    $creditOnlyInTheGatedPostfix = $observed -ge 0 -and $gate -gt $observed -and $credit -gt $gate
    # The sentry host gate is read from the stripped text too, and the stripped text must hold it in exactly one place:
    # the file also prints `SNet.IsMaster` in two diagnostic strings, and a gate named in a comment is not a gate.
    $toolStatsSource = Get-Content (Join-Path $projectRoot 'ToolStatsTracker.cs') -Raw
    $toolStatsCode = (($toolStatsSource -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n"
    $sentryHostGate = ([regex]::Matches($toolStatsCode, [regex]::Escape('!SNet.IsMaster'))).Count -eq 1
    if (-not $usesTargetPolicy -or -not $oneExclusion -or -not $oneSeedPrefix -or -not $seedCreditsNothing -or `
        -not $creditOnlyInTheGatedPostfix -or -not $sentryHostGate) {
        throw 'The blood-loss observation must exclude exactly ReceiveParasiteDamage (and name no other method), seed the watermark only from the calls that report a hit and only through the one SeedBefore entry point, credit nothing in the seed path, credit in the level-gated postfix alone, observe every accepted method through the one postfix, and leave the sentry host gate in place.'
    }
    Write-Host 'HEALTH_LOSS_TARGET_WIRING: PASS'
    # A hello that arrives before the roster holds its sender is kept and re-judged, never accepted early.
    $syncSource = Get-Content (Join-Path $projectRoot 'CombatSync.cs') -Raw
    $exchangeSource = Get-Content (Join-Path $projectRoot 'SquadExchange.cs') -Raw
    $defersHellos = $exchangeSource.Contains('DeferHello(') -and $exchangeSource.Contains('TryTakeDeferredHello(')
    $rejudgesAfterRoster = $syncSource.Contains('RetryDeferredHellos();') -and $syncSource.Contains('Exchange.SetRoster(')
    # A hello kept from one run must not be re-judged against the next run's roster: the explicit expedition reset is
    # wired from `CombatSync.ResetExpedition`, and the smoke suite asserts that it actually empties the list.
    $dropsHellosOnReset = $syncSource.Contains('Exchange.ResetForExpedition();')
    if (-not $defersHellos -or -not $rejudgesAfterRoster -or -not $dropsHellosOnReset) {
        throw 'A hello refused for an older roster must be re-judged against the refreshed roster instead of being dropped, and the kept hellos must be dropped on the expedition reset.'
    }
    Write-Host 'SQUAD_HELLO_ROSTER_RACE: PASS'
    # Static source gate for the rule 1.19.38 replaced: `SquadCapability.EvidenceOf` used to end in
    # `if (_broadcast) return SquadEvidence.Broadcast;`, so every unproven peer in the slot list was spoken to for the
    # whole session - an unmodded machine's GTFO-API wrote one `[Error ...] which isn't registered` line per packet
    # (10225 of them in one 13051-line log). The rule now is a bounded probe, and it must not drift back.
    # This is a static source check, not a runtime behaviour proof: it reads the file, it never runs the gate.
    $capabilitySource = Get-Content (Join-Path $projectRoot 'SquadCapability.cs') -Raw
    $noUnprovenPass = -not $capabilitySource.Contains('return SquadEvidence.Broadcast;') -and `
        -not $capabilitySource.Contains('Broadcast = 3')
    $keepsBoundedProbe = $capabilitySource.Contains('ProbesSent(peer) < ProbeBudget') -and `
        $capabilitySource.Contains('if (_allowlist.Contains(peer)) return SquadEvidence.Allowlisted;')
    if (-not $noUnprovenPass -or -not $keepsBoundedProbe) {
        throw 'Static source check: EvidenceOf must not contain an unproven-pass branch (no `return SquadEvidence.Broadcast;`, no `Broadcast = 3`), and must keep the bounded probe (`ProbesSent(peer) < ProbeBudget`) behind the allowlist. A peer that has not proved itself is probed a bounded number of times, never broadcast to.'
    }
    Write-Host 'SQUAD_PROBE_BUDGET_STATIC: PASS (static source check; not a runtime behaviour proof)'
    $fontSource = Get-Content (Join-Path $projectRoot 'HudTable.cs') -Raw
    $hasTmpDefault = $fontSource.Contains('TMP_Settings.defaultFontAsset')
    $reusesTmpSourceFont = $fontSource.Contains('sourceFontFile')
    $checksDirectCjkGlyphs = $fontSource.Contains("HasCharacter('任', false, false)")
    if (-not $hasTmpDefault -or -not $reusesTmpSourceFont -or -not $checksDirectCjkGlyphs) {
        throw 'Shared table font must resolve from a GTFO TMP asset with direct CJK glyphs.'
    }
    foreach ($file in @('PanelBehaviour.cs', 'ResultsDisplay.cs')) {
        if (-not (Get-Content (Join-Path $projectRoot $file) -Raw).Contains('HudTable.Draw')) {
            throw "Both overlay surfaces must use the shared GTFO TMP font renderer: $file"
        }
    }
    Write-Host 'GTFO_TMP_FONT_WIRING: PASS'
    # 1.19.65: a client's own effective enemy damage is spent from ONE balance per enemy instance, and an estimate the
    # enemy's own confirmed reading cannot support is taken back off the board again. The rules are pure and asserted by
    # the smoke suite; these lines assert the runtime routes both estimate entry points through the balance, that the
    # correction acts on the same three places the credit went, that the two client-side damage entrances consume a
    # pending spawn first, that the host's authority is still refused at its one place, and that the balance is cleared
    # with the expedition and on an enemy's own spawn edge. Comments are stripped first, so a rule read out of a comment
    # is not a rule.
    $budgetSource = Get-Content (Join-Path $projectRoot 'OptionalDamageEvents.cs') -Raw
    $budgetCode = (($budgetSource -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n"
    $trackerSource = Get-Content (Join-Path $projectRoot 'CombatTracker.cs') -Raw
    $trackerCode = (($trackerSource -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n"
    $statsCode = (($((Get-Content (Join-Path $projectRoot 'CombatStats.cs') -Raw) -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n")
    $impactCode = (($((Get-Content (Join-Path $projectRoot 'ImpactMetrics.cs') -Raw) -split "`n") | ForEach-Object { ($_ -split '//')[0] }) -join "`n")
    $oneBalancePerEnemy = $budgetCode.Contains('internal sealed class EnemyHealthBudget') -and `
        $budgetCode.Contains('taken = Math.Min(wanted, row.Remaining)') -and `
        $budgetCode.Contains('if (attributable) withdrawn = WithdrawUnsupported(row, healthBefore);') -and `
        $budgetCode.Contains('if (health < row.Remaining) row.Opened = health + row.Credited;') -and `
        $budgetCode.Contains('bool reused = row != null && owner != IntPtr.Zero && row.Owner != IntPtr.Zero && row.Owner != owner;')
    $correctionReachesEveryPlace = $budgetCode.Contains('estimate.Player?.Total.WithdrawEnemyDamage(take);') -and `
        $budgetCode.Contains('estimate.Weapon?.Counters.WithdrawEnemyDamage(take);') -and `
        $budgetCode.Contains('if (estimate.Scout) estimate.Player?.Impact.WithdrawScoutDamage(take);') -and `
        $budgetCode.Contains('if (!attributable && player != null)') -and `
        $statsCode.Contains('public void WithdrawEnemyDamage(double amount)') -and `
        $impactCode.Contains('public void WithdrawScoutDamage(double amount)')
    $localPathUsesTheBalance = $trackerCode.Contains('Budget.Charge(limb.m_base.Pointer, OwnerPointer(limb.m_base), player, weapon, scout,') -and `
        $trackerCode.Contains('LocalOutgoingRule.Amount(healthBefore, healthAfter, dam), measured,') -and `
        -not $trackerCode.Contains('float amount = (float)LocalOutgoingRule.Amount(')
    $ewcPathUsesTheBalance = $trackerCode.Contains('bool client = !SNet.IsMaster && !CombatSync.HostRunsMod;') -and `
        $trackerCode.Contains('amount = Budget.Charge(target.Pointer, OwnerPointer(target), sample.Player, sample.Weapon, scout,') -and `
        $trackerCode.Contains('sample.HealthBefore, healthAfter, residual, measured, out withdrawn);')
    $hostAuthorityKept = ([regex]::Matches($trackerCode, [regex]::Escape('if (SNet.IsMaster || CombatSync.HostRunsMod) return;'))).Count -eq 1 -and `
        ([regex]::Matches($trackerCode, [regex]::Escape('bool client = !SNet.IsMaster && !CombatSync.HostRunsMod;'))).Count -eq 1
    $pendingSpawnConsumedFirst = ([regex]::Matches($trackerCode, [regex]::Escape('ConsumePendingSpawn('))).Count -eq 3 -and `
        $trackerCode.Contains('private static void ConsumePendingSpawn(Dam_EnemyDamageBase damage)') -and `
        $trackerCode.Contains('!PendingEnemySpawns.Take(damage.Owner.Pointer)') -and `
        $trackerCode.Contains('ConsumePendingSpawn(limb.m_base);') -and `
        $trackerCode.Contains('if (client) ConsumePendingSpawn(target);')
    $balanceResetWiring = $trackerCode.Contains('EnemyCredits.Clear(); Budget.Clear();') -and `
        $trackerCode.Contains('Deaths.Spawn(pointer); EnemyCredits.Spawn(pointer); Budget.Spawn(pointer);')
    if (-not $oneBalancePerEnemy -or -not $correctionReachesEveryPlace -or -not $localPathUsesTheBalance -or `
        -not $ewcPathUsesTheBalance -or -not $hostAuthorityKept -or -not $pendingSpawnConsumedFirst -or -not $balanceResetWiring) {
        throw 'The client''s own damage estimate must be spent from one balance per enemy instance (`EnemyHealthBudget`), an attributable reading must be able to take back only the estimates it cannot support - on the player''s total, the weapon row and the Scout row alike, never a confirmed credit, and never on an unattributed reading - both estimate entry points must route through it behind the host refusal at its one place, the two client-side entrances must consume a pending spawn before touching anything keyed by a pointer, and the balance must be cleared with the expedition and on an enemy''s own spawn edge.'
    }
    Write-Host 'LOCAL_DAMAGE_BALANCE_WIRING: PASS (static source check; the rules themselves are asserted by the smoke suite)'
    Write-Host 'VERIFY: PASS'
}
finally {
    Pop-Location
}
