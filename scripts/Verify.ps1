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

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.name -ne 'CombatPanel' -or $manifest.version_number -ne '1.18.0') {
        throw 'Thunderstore package name/version mismatch.'
    }
    if ($manifest.dependencies.Count -ne 1 -or $manifest.dependencies[0] -ne 'BepInEx-BepInExPack_GTFO-3.2.3') {
        throw 'Package must depend on the BepInEx GTFO pack only.'
    }
    if ($manifest.description.Length -gt 256) {
        throw "Thunderstore rejects a manifest description longer than 256 characters; this one is $($manifest.description.Length)."
    }
    foreach ($file in @('PanelBehaviour.cs', 'ResultsDisplay.cs')) {
        if (-not (Get-Content (Join-Path $projectRoot $file) -Raw).Contains('CombatSync.DisplayRows().OrderBy(')) {
            throw "Display must consume the shared squad projection: $file"
        }
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
