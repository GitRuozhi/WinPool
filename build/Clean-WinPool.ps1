<#
.SYNOPSIS
Preserves selected WinPool build outputs without deleting data or test evidence.
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$BuildIntermediatesOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Preserve-GeneratedOutput.ps1')
. (Join-Path $PSScriptRoot 'Assert-RealOperationIdle.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'

# Never terminate unrelated instances or device-properties hosts. The caller
# closes the exact App/Agent first; a busy runtime is a clear refusal.
Assert-WinPoolRuntimeStopped $artifactsRoot
foreach ($configuration in @('Debug', 'Release')) {
    $candidate = Join-Path $artifactsRoot $configuration
    if (Test-Path -LiteralPath $candidate) {
        Assert-WinPoolRealOperationIdle -RuntimeRoot $candidate
    }
}
$targets = [Collections.Generic.List[string]]::new()
foreach ($name in @('build', 'obj', 'trees')) {
    $targets.Add((Join-Path $artifactsRoot $name))
}
foreach ($configuration in $(if ($BuildIntermediatesOnly) { @() } else { @('Debug', 'Release') })) {
    $runtime = Join-Path $artifactsRoot $configuration
    if (Test-Path -LiteralPath $runtime) {
        Assert-WinPoolTreeHasNoLinks $runtime
        foreach ($entry in Get-ChildItem -LiteralPath $runtime -Force) {
            # Keep the portable data root at its selected location.
            if ($entry.Name -ne 'Data') { $targets.Add($entry.FullName) }
        }
    }
}
foreach ($area in @('src', 'workers', 'tests')) {
    $areaRoot = Join-Path $repositoryRoot $area
    if (-not (Test-Path -LiteralPath $areaRoot)) { continue }
    foreach ($directory in Get-ChildItem -LiteralPath $areaRoot -Directory -Recurse) {
        if ($directory.Name -in @('bin', 'obj') -and
            @(Get-ChildItem -LiteralPath $directory.Parent.FullName -Filter '*.csproj' -File).Count -gt 0) {
            $targets.Add($directory.FullName)
        }
    }
}
if (-not $BuildIntermediatesOnly) { $targets.Add((Join-Path $repositoryRoot 'WinPool.lnk')) }
foreach ($target in $targets) {
    if ((Test-Path -LiteralPath $target) -and $PSCmdlet.ShouldProcess($target, 'Move generated output to workspace Rubbish')) {
        $preserved = Move-WinPoolGeneratedOutput $target
        Write-Output "Preserved: $preserved"
    }
}
Write-Output 'Generated outputs processed. Portable Data, test-results, and other evidence directories remain in place.'
