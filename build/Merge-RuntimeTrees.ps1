[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AppDir,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AgentDir,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Destination,

    [switch]$SkipPdb,

    [switch]$ReplaceDestination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Preserve-GeneratedOutput.ps1')
. (Join-Path $PSScriptRoot 'Assert-RealOperationIdle.ps1')

$appRoot = Assert-WinPoolCheckoutPath $AppDir
$agentRoot = Assert-WinPoolCheckoutPath $AgentDir
$destinationRoot = Assert-WinPoolCheckoutPath $Destination

if (-not (Test-Path -LiteralPath $appRoot)) {
    throw "App runtime tree was not found: $appRoot"
}
if (-not (Test-Path -LiteralPath $agentRoot)) {
    throw "Agent runtime tree was not found: $agentRoot"
}

$normalizedApp = $appRoot.TrimEnd('\', '/')
$normalizedAgent = $agentRoot.TrimEnd('\', '/')
if ($destinationRoot -eq $normalizedApp -or $destinationRoot -eq $normalizedAgent) {
    throw "Destination must be independent of the App and Agent trees: $destinationRoot"
}
foreach ($sourceRoot in @($normalizedApp, $normalizedAgent)) {
    if ($destinationRoot.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($destinationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime sources and destination must not contain each other.'
    }
    Assert-WinPoolTreeHasNoLinks $sourceRoot
}
Assert-WinPoolRuntimeStopped $destinationRoot
Assert-WinPoolRealOperationIdle -RuntimeRoot $destinationRoot
Assert-WinPoolTreeHasNoLinks $destinationRoot

function Get-PublishFileMap([string]$root) {
    $map = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File)) {
        if ($SkipPdb -and $file.Extension -eq '.pdb') {
            continue
        }

        $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if ($map.ContainsKey($relative)) {
            throw "Duplicate relative path in runtime tree ${root}: $relative"
        }

        $map[$relative] = $file.FullName
    }

    return $map
}

function Copy-UnionFile([string]$sourcePath, [string]$destinationPath) {
    $directory = Split-Path -Parent $destinationPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

function Merge-PublishTrees([string]$appPublish, [string]$agentPublish, [string]$mergeRoot) {
    if (-not (Test-Path -LiteralPath $mergeRoot)) {
        New-Item -ItemType Directory -Path $mergeRoot | Out-Null
    }

    $appMap = Get-PublishFileMap $appPublish
    $agentMap = Get-PublishFileMap $agentPublish
    $relativePaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $appMap.Keys) {
        [void]$relativePaths.Add($key)
    }
    foreach ($key in $agentMap.Keys) {
        [void]$relativePaths.Add($key)
    }

    $sharedCount = 0
    $appOnlyCount = 0
    $agentOnlyCount = 0

    foreach ($relative in ($relativePaths | Sort-Object)) {
        $inApp = $appMap.ContainsKey($relative)
        $inAgent = $agentMap.ContainsKey($relative)
        $destinationPath = Join-Path $mergeRoot ($relative.Replace('/', '\'))

        if ($inApp -and $inAgent) {
            $appHash = (Get-FileHash -LiteralPath $appMap[$relative] -Algorithm SHA256).Hash
            $agentHash = (Get-FileHash -LiteralPath $agentMap[$relative] -Algorithm SHA256).Hash
            if ($appHash -ne $agentHash) {
                throw "Runtime union collision: $relative has different SHA-256 hashes (App=$appHash, Agent=$agentHash)."
            }

            Copy-UnionFile $appMap[$relative] $destinationPath
            $sharedCount += 1
        }
        elseif ($inApp) {
            Copy-UnionFile $appMap[$relative] $destinationPath
            $appOnlyCount += 1
        }
        else {
            Copy-UnionFile $agentMap[$relative] $destinationPath
            $agentOnlyCount += 1
        }
    }

    Write-Output "Union merge: $sharedCount shared, $appOnlyCount App-only, $agentOnlyCount Agent-only, 0 collisions"
}

$mergeRoot = $destinationRoot
$temporaryRoot = $null
if ($ReplaceDestination) {
    $temporaryRoot = Join-Path (Split-Path -Parent $destinationRoot) (
        (Split-Path -Leaf $destinationRoot) + '.merge-' + [guid]::NewGuid().ToString('N'))
    $mergeRoot = $temporaryRoot
}

try {
    Merge-PublishTrees $appRoot $agentRoot $mergeRoot
    if ($ReplaceDestination) {
        # Portable data belongs to the user, not to the generated runtime.
        $portableData = Join-Path $destinationRoot 'Data'
        if (Test-Path -LiteralPath $portableData) {
            $newData = Join-Path $temporaryRoot 'Data'
            if (Test-Path -LiteralPath $newData) { throw 'The generated runtime unexpectedly contains Data.' }
            Copy-Item -LiteralPath $portableData -Destination $newData -Recurse -Force
        }
        Assert-WinPoolRuntimeStopped $destinationRoot
        Assert-WinPoolRealOperationIdle -RuntimeRoot $destinationRoot
        $previousRoot = $null
        if (Test-Path -LiteralPath $destinationRoot) {
            $previousRoot = Move-WinPoolGeneratedOutput $destinationRoot
        }
        try {
            Move-Item -LiteralPath $temporaryRoot -Destination $destinationRoot -ErrorAction Stop
            $temporaryRoot = $null
        }
        catch {
            if ($previousRoot -and -not (Test-Path -LiteralPath $destinationRoot)) {
                Move-Item -LiteralPath $previousRoot -Destination $destinationRoot -ErrorAction Stop
                if (-not (Test-Path -LiteralPath $destinationRoot) -or (Test-Path -LiteralPath $previousRoot)) {
                    throw "Runtime rollback could not be verified: $previousRoot"
                }
            }
            throw
        }
        if ($previousRoot) { Write-Output "Previous runtime preserved: $previousRoot" }
    }
}
finally {
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        $preserved = Move-WinPoolGeneratedOutput $temporaryRoot
        Write-Output "Unpublished runtime preserved: $preserved"
    }
}
