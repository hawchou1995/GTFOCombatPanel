[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EwcDll,
    [Parameter(Mandatory)][string]$CecilDll
)
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CecilDll).Path) | Out-Null
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $EwcDll).Path)
try {
    $module = $assembly.MainModule
    # Types are located by simple name: 4.13.4 moved IOwnerComp/IWeaponComp out of the OwnerComps /
    # WeaponComps child namespaces, and the bridge reads these members by name at runtime anyway.
    function Find-Type([string]$simpleName) {
        foreach ($type in $module.Types) {
            if ($type.Name -eq $simpleName) { return $type }
            foreach ($nested in $type.NestedTypes) { if ($nested.Name -eq $simpleName) { return $nested } }
        }
        return $null
    }

    $api = $module.GetType('EWC.API.ProjectileAPI')
    if ($null -eq $api) { throw 'EWC.API.ProjectileAPI is missing.' }
    $componentName = 'EWC.CustomWeapon.Properties.Traits.CustomProjectile.Components.EWCProjectileComponentBase'
    foreach ($name in @('OnProjectileSpawned', 'OnProjectileHit', 'OnProjectilePostHit', 'OnProjectileDestroyed')) {
        $event = $api.Events | Where-Object Name -EQ $name | Select-Object -First 1
        if ($null -eq $event -or -not $event.AddMethod.IsPublic -or -not $event.AddMethod.IsStatic) { throw "Public static event is missing: $name" }
        $invoke = $event.EventType.Resolve().Methods | Where-Object Name -EQ 'Invoke' | Select-Object -First 1
        $expected = if ($name -in @('OnProjectileHit', 'OnProjectilePostHit')) { @($componentName, 'IDamageable') } else { @($componentName) }
        $actual = @($invoke.Parameters | ForEach-Object { $_.ParameterType.FullName })
        if ($invoke.ReturnType.FullName -ne 'System.Void' -or ($actual -join '|') -ne ($expected -join '|')) {
            throw "EWC event signature mismatch: $name ($($actual -join ', '))"
        }
        Write-Host "EWC_EVENT: PASS $name($($actual -join ', '))"
    }
    $damageApi = $module.GetType('EWC.API.DamageAPI')
    if ($null -eq $damageApi) { throw 'EWC.API.DamageAPI is missing.' }
    foreach ($kind in @('Explosive', 'Shrapnel', 'DOT')) {
        foreach ($phase in @('Pre', 'Post')) {
            $name = $phase + $kind + 'Damage'
            $event = $damageApi.Events | Where-Object Name -EQ $name | Select-Object -First 1
            if ($null -eq $event -or -not $event.AddMethod.IsPublic -or -not $event.AddMethod.IsStatic) { throw "Public static event is missing: $name" }
            $invoke = $event.EventType.Resolve().Methods | Where-Object Name -EQ 'Invoke' | Select-Object -First 1
            $actual = @($invoke.Parameters | ForEach-Object { $_.ParameterType.FullName })
            $expected = @('System.Single', 'Enemies.EnemyAgent', 'Dam_EnemyDamageLimb', 'Player.PlayerAgent', 'EWC.CustomWeapon.Structs.pCWC')
            if ($invoke.ReturnType.FullName -ne 'System.Void' -or ($actual -join '|') -ne ($expected -join '|')) { throw "EWC damage signature mismatch: $name ($($actual -join ', '))" }
            Write-Host "EWC_DAMAGE_EVENT: PASS $name"
        }
    }

    # Every entry is the exact member the bridge reads at runtime, by declaring type and by name.
    $members = @(
        @('EWCProjectileComponentBase', 'IsManaged', 'Boolean'),
        @('EWCProjectileComponentBase', 'PlayerIndex', 'UInt16'),
        @('EWCProjectileComponentBase', 'SyncID', 'UInt16'),
        @('EWCProjectileComponentBase', 'ShotIndex', 'UInt16'),
        @('EWCProjectileComponentBase', 'Settings', 'Projectile'),
        @('WeaponPropertyBase', 'CWC', 'CustomWeaponComponent'),
        @('CustomWeaponComponent', 'Owner', 'IOwnerComp'),
        @('CustomWeaponComponent', 'Weapon', 'IWeaponComp'),
        @('IOwnerComp', 'Player', 'PlayerAgent'),
        @('IWeaponComp', 'InventorySlot', 'InventorySlot'),
        @('pCWC', 'slot', 'InventorySlot')
    )
    foreach ($spec in $members) {
        $type = Find-Type $spec[0]
        if ($null -eq $type) { throw "EWC type is missing: $($spec[0])" }
        $property = $type.Properties | Where-Object Name -EQ $spec[1] | Select-Object -First 1
        $field = $type.Fields | Where-Object Name -EQ $spec[1] | Select-Object -First 1
        $actual = if ($null -ne $property -and $property.GetMethod.IsPublic) { $property.PropertyType.Name }
                  elseif ($null -ne $field -and $field.IsPublic) { $field.FieldType.Name }
                  else { '' }
        if ($actual -ne $spec[2]) { throw "EWC member mismatch: $($spec[0]).$($spec[1]); expected=$($spec[2]); actual=$actual" }
    }
    $cwc = Find-Type 'CustomWeaponComponent'
    if ($null -eq $cwc -or $cwc.BaseType.FullName -ne 'UnityEngine.MonoBehaviour') { throw 'CWC is not a Unity component.' }
    $strongName = $assembly.Name
    Write-Host "EWC_API_CONTRACT: PASS; assembly=$($strongName.Name) $($strongName.Version); public events=10; bridge members=11; SHA256=$((Get-FileHash -LiteralPath $EwcDll -Algorithm SHA256).Hash)"
}
finally { $assembly.Dispose() }
