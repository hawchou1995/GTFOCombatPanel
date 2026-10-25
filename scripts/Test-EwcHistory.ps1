param([Parameter(Mandatory)][string[]]$Dlls, [string]$CecilPath)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CecilPath)) {
    if ([string]::IsNullOrWhiteSpace($env:GTFO_DIR)) { throw 'Pass -CecilPath, or set GTFO_DIR to a profile root, so BepInEx\core\Mono.Cecil.dll can be found.' }
    $CecilPath = Join-Path $env:GTFO_DIR 'BepInEx\core\Mono.Cecil.dll'
}
Add-Type -Path (Resolve-Path -LiteralPath $CecilPath).Path
foreach ($entry in $Dlls) {
    $name, $path = $entry -split '\|', 2
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $path).Path)
    try {
        $module = $assembly.MainModule
        $types = @($module.Types | ForEach-Object Name)
        $refs = @($module.AssemblyReferences | ForEach-Object Name)
        [pscustomobject]@{
            Label              = $name
            Version            = $assembly.Name.Version.ToString()
            EwcBridge          = $types -contains 'EwcBridge'
            EwcProjectileBridge = $types -contains 'EwcProjectileBridge'
            ForcedEwcReference = $refs -contains 'ExtraWeaponCustomization'
            Sha256             = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.Substring(0, 16)
        }
    }
    finally { $assembly.Dispose() }
}
