param([Parameter(Mandatory)][string[]]$Dlls)
$ErrorActionPreference = 'Stop'
Add-Type -Path 'E:\Games\R2ModMan\GTFO\profiles\Test\BepInEx\core\Mono.Cecil.dll'
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
