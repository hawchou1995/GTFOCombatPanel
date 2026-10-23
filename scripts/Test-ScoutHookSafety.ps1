[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PluginDll,
    [Parameter(Mandatory)][string]$CecilDll
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilDll
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $PluginDll).Path)
function Test-UnsafeScoutName([string]$name) {
    return $name -match '(^|[^A-Za-z0-9_])ES_(Scout[A-Za-z0-9_]*|Base)([^A-Za-z0-9_]|$)'
}
function Test-UnsafeScoutType($type) {
    return $null -ne $type -and (Test-UnsafeScoutName $type.FullName)
}
function Get-AllTypes($types) {
    foreach ($type in $types) {
        $type
        Get-AllTypes $type.NestedTypes
    }
}
try {
    $patches = 0
    foreach ($type in (Get-AllTypes $assembly.MainModule.Types)) {
        $isPatch = $false
        foreach ($provider in @($type) + @($type.Methods)) {
            foreach ($attribute in $provider.CustomAttributes) {
                if ($attribute.AttributeType.FullName -ne 'HarmonyLib.HarmonyPatch') { continue }
                $isPatch = $true
                foreach ($argument in $attribute.ConstructorArguments) {
                    if (($argument.Value -is [Mono.Cecil.TypeReference] -and (Test-UnsafeScoutType $argument.Value)) -or
                        ($argument.Value -is [string] -and (Test-UnsafeScoutName $argument.Value))) {
                        throw "Unsafe Scout Harmony attribute: $($type.FullName)"
                    }
                }
            }
        }
        if ($type.Name -eq 'ScoutWitness' -and @($type.Methods | Where-Object { $_.Name -eq 'Owner' }).Count -gt 0) {
            throw 'Unsafe Scout state-to-owner reverse lookup remains in compiled DLL.'
        }
        if ($isPatch) { $patches++ }
        # Scan helpers and generated iterators too: moving a target out of a patch is not a fix.
        foreach ($method in $type.Methods) {
            foreach ($parameter in $method.Parameters) {
                if (Test-UnsafeScoutType $parameter.ParameterType) { throw "Unsafe Scout state parameter: $($method.FullName)" }
            }
            if (-not $method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                if ($operand -is [string] -and (Test-UnsafeScoutName $operand)) {
                    throw "Unsafe Scout target string: $($method.FullName)"
                }
                if ($operand -is [Mono.Cecil.TypeReference] -and (Test-UnsafeScoutType $operand)) {
                    throw "Unsafe Scout type token in patch or helper: $($method.FullName)"
                }
                if ($operand -is [Mono.Cecil.MemberReference]) {
                    $declaring = $operand.DeclaringType
                    if (($declaring.FullName -match '(^|\.)ES_Base$') -or ($isPatch -and (Test-UnsafeScoutType $declaring))) {
                        throw "Unsafe Scout state member access: $($method.FullName) -> $($declaring.FullName)"
                    }
                }
            }
        }
    }
    if ($patches -eq 0) { throw 'No Harmony patches found; refusing an empty safety scan.' }
    Write-Host "SCOUT_HOOK_SAFETY: PASS ($patches compiled patch classes; global helper/token/string/parameter checks; no Scout detours or reverse owner lookup)"
}
finally { $assembly.Dispose() }
