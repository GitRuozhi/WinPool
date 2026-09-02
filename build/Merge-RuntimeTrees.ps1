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

$appRoot = [System.IO.Path]::GetFullPath($AppDir)
$agentRoot = [System.IO.Path]::GetFullPath($AgentDir)
$destinationRoot = [System.IO.Path]::GetFullPath($Destination.TrimEnd('\', '/'))

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
        if (Test-Path -LiteralPath $destinationRoot) {
            Remove-Item -LiteralPath $destinationRoot -Recurse -Force
        }

        Move-Item -LiteralPath $temporaryRoot -Destination $destinationRoot
        $temporaryRoot = $null
    }
}
finally {
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
