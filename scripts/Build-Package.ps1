[CmdletBinding()]
param(
    [string]$DotnetExe = 'dotnet',
    [string]$GTFO_DIR = $env:GTFO_DIR,
    [string]$BuildOutputDirectory,
    [switch]$DisableEwcBridge
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildOutputDirectory)) { $BuildOutputDirectory = Join-Path $projectRoot 'bin\Release' }
$BuildOutputDirectory = [IO.Path]::GetFullPath($BuildOutputDirectory)
& (Join-Path $PSScriptRoot 'Verify.ps1') -DotnetExe $DotnetExe -GTFO_DIR $GTFO_DIR -BuildOutputDirectory $BuildOutputDirectory -DisableEwcBridge:$DisableEwcBridge
if ($LASTEXITCODE -ne 0) { throw "Verification failed with exit code $LASTEXITCODE" }

# The scoring file this build creates on a fresh install. It is emitted by the smoke suite itself, so the
# copy that ships is byte-identical to the one the running mod would write, and can never drift from the
# defaults the code carries.
$defaultsDir = Join-Path $projectRoot 'defaults'
$testProject = Join-Path $projectRoot 'tests\GTFOCombatPanel.Smoke\GTFOCombatPanel.Smoke.csproj'
& $DotnetExe run --project $testProject --configuration Release --verbosity quiet -- --emit-default-scoring $defaultsDir
if ($LASTEXITCODE -ne 0) { throw "Emitting the default scoring file failed with exit code $LASTEXITCODE" }
$scoringJson = Join-Path $defaultsDir 'scoring.json'
if (-not (Test-Path -LiteralPath $scoringJson -PathType Leaf)) { throw "Default scoring file was not written: $scoringJson" }

$packageSource = Join-Path $projectRoot 'Thunderstore'
$pluginDll = Join-Path $BuildOutputDirectory 'GTFOCombatPanel.dll'
$artifactDir = Join-Path $projectRoot 'artifacts'
$packageVersion = (Get-Content (Join-Path $packageSource 'manifest.json') -Raw | ConvertFrom-Json).version_number
$zipPath = Join-Path $artifactDir ("GTFOCombatPanel-" + $packageVersion + ".zip")
$required = @(
    (Join-Path $packageSource 'manifest.json'),
    (Join-Path $packageSource 'README.md'),
    (Join-Path $packageSource 'CHANGELOG.md'),
    (Join-Path $packageSource 'icon.png'),
    $scoringJson,
    $pluginDll
)
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required package input is missing: $file" }
}

New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$archive = $null
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $packageSource 'manifest.json'), 'manifest.json', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $packageSource 'README.md'), 'README.md', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $packageSource 'icon.png'), 'icon.png', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $pluginDll, 'BepInEx/plugins/GTFOCombatPanel/GTFOCombatPanel.dll', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $packageSource 'CHANGELOG.md'), 'CHANGELOG.md', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    # The scoring file travels next to the DLL, in its own sub folder. Installers must only drop it in when
    # it is absent, so a user who has edited it keeps their copy.
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $scoringJson, 'BepInEx/plugins/GTFOCombatPanel/CombatPanel-config/scoring.json', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    if ($archive -ne $null) { $archive.Dispose() } else { $stream.Dispose() }
}

$expected = @(
    'manifest.json',
    'README.md',
    'CHANGELOG.md',
    'icon.png',
    'BepInEx/plugins/GTFOCombatPanel/CombatPanel-config/scoring.json',
    'BepInEx/plugins/GTFOCombatPanel/GTFOCombatPanel.dll'
)
$check = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($check.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $expectedSorted = @($expected | Sort-Object)
    if (Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actual) {
        throw 'Package archive contents do not match the exact expected file set.'
    }
    $manifestEntry = $check.GetEntry('manifest.json')
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $archiveManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($archiveManifest.dependencies.Count -ne 1 -or $archiveManifest.dependencies[0] -ne 'BepInEx-BepInExPack_GTFO-3.2.3') {
        throw 'Archived manifest contains an unexpected dependency.'
    }
    $dllEntries = @($check.Entries | Where-Object { $_.FullName -match '\.dll$' })
    if ($dllEntries.Count -ne 1 -or $dllEntries[0].FullName -ne 'BepInEx/plugins/GTFOCombatPanel/GTFOCombatPanel.dll') {
        throw 'Package must contain only its own plugin DLL.'
    }
}
finally { $check.Dispose() }

$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Write-Host "PACKAGE=$zipPath"
Write-Host "SHA256=$sha256"
Write-Host 'PACKAGE_CONTENTS:'
& tar.exe -tf $zipPath
if ($LASTEXITCODE -ne 0) { throw "Unable to list archive (exit $LASTEXITCODE)" }
