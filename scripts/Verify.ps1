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
    if ($manifest.dependencies.Count -ne 1 -or $manifest.dependencies[0] -ne 'BepInEx-BepInExPack_GTFO-3.2.3') {
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
    # The settlement board's upper bound: the completion section is measured as the visible pixels it draws, and
    # the table hangs from that edge instead of being bottom-aligned against the lobby button. Both are
    # behavioural properties the headless suite cannot reach (they need the real page), so the wiring is checked
    # here and the policy itself is asserted in tests/GTFOCombatPanel.Smoke.
    $usesVisibleCompletion = $resultsSource.Contains('CompletionBounds.Deepest(')
    $anchorsOnUpperEdge = $resultsSource.Contains('bottomAligned: false') -and -not $resultsSource.Contains('bottomAligned: true')
    $namesTheAnchor = $resultsSource.Contains('FailureAlignment.Source(') -and $resultsSource.Contains('anchorSource=')
    $boundsTheTitleWait = $resultsSource.Contains('FailureAlignment.WaitForTitle(')
    # A world-space renderer is measured from its own bounds, so the projection must go through `ScreenBounds` and
    # must not sit behind a RectTransform any more; and a success page whose completion section could not be measured
    # is deferred (`ResultsUpperBound.Decide`) instead of being placed at the historical fraction of the window,
    # which sits above the icons and captions. The smoke suite asserts both policies; these two lines assert that the
    # runtime actually calls them.
    $measuresRendererBounds = $resultsSource.Contains('ScreenBounds.TryFromCorners(') -and $resultsSource.Contains('renderer.bounds')
    $defersWithoutMeasuredSection = $resultsSource.Contains('ResultsUpperBound.Decide(') -and -not $resultsSource.Contains('ResultsRegion.Resolve(false')
    $upperBoundBroken = (-not $usesVisibleCompletion) -or (-not $anchorsOnUpperEdge) -or (-not $namesTheAnchor) -or (-not $boundsTheTitleWait) -or (-not $measuresRendererBounds) -or (-not $defersWithoutMeasuredSection)
    if ($upperBoundBroken) {
        throw 'The result board must measure the completion section by its visible pixels (renderer bounds projected through ScreenBounds, never behind a RectTransform), hang the table from that edge, defer instead of guessing when that edge is unmeasurable, name its anchor and bound the wait for the failure title.'
    }
    Write-Host 'RESULT_BOARD_UPPER_BOUND: PASS'
    # The tool cell is chosen from the recorded figures first (the name is only a hint), and a slot is read from
    # its most specific real row rather than from the first entry the dictionary happens to yield.
    $toolDetailFromData = (Get-Content (Join-Path $projectRoot 'ImpactBoard.cs') -Raw).Contains('ToolDetailOf(counters, name)')
    $slotRowPolicy = $resultsSource.Contains('WeaponRows.For(player.Weapons, (int)slot)')
    if (-not $toolDetailFromData -or -not $slotRowPolicy) {
        throw 'The tool cell and the tool slot row must both be decided by recorded figures and the most specific real row.'
    }
    Write-Host 'SETTLEMENT_TOOL_DATA_FIRST: PASS'
    # The parasite-damage crash mitigation is one exclusion and nothing else: the target set is decided by
    # `HealthLossTargets`, the patch class adds no prefix and no re-entry guard, and the host gate on the sentry
    # ledger is untouched.
    $healthSource = Get-Content (Join-Path $projectRoot 'HealthLossPatches.cs') -Raw
    $targetsSource = Get-Content (Join-Path $projectRoot 'HealthLossLedger.cs') -Raw
    $usesTargetPolicy = $healthSource.Contains('HealthLossTargets.Accepts(') -and -not $healthSource.Contains('[HarmonyPrefix')
    $oneExclusion = ([regex]::Matches($targetsSource, '"Receive[A-Za-z]+"')).Count -eq 1 -and $targetsSource.Contains('"ReceiveParasiteDamage"')
    $sentryHostGate = (Get-Content (Join-Path $projectRoot 'ToolStatsTracker.cs') -Raw).Contains('!SNet.IsMaster')
    if (-not $usesTargetPolicy -or -not $oneExclusion -or -not $sentryHostGate) {
        throw 'The blood-loss observation must exclude exactly ReceiveParasiteDamage, add no prefix, and leave the sentry host gate in place.'
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
    Write-Host 'VERIFY: PASS'
}
finally {
    Pop-Location
}
