<#
.SYNOPSIS
Archives and resets the current user's WinPool development data.

.DESCRIPTION
Stops WinPool processes, moves %LocalAppData%\WinPool into the parent project's
Rubbish tree, and verifies that the source is gone and the recoverable archive
contains the same file count and byte count. No data is directly deleted.

Use -WhatIf to preview the exact source and archive paths without changing state.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '..\..'))
$rubbishRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'Rubbish'))
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')

if ([string]::IsNullOrWhiteSpace($localAppData)) {
    throw 'The current-user LocalAppData directory is unavailable.'
}

$localAppData = [System.IO.Path]::GetFullPath($localAppData)
$dataRoot = [System.IO.Path]::GetFullPath((Join-Path $localAppData 'WinPool'))
$expectedDataRoot = Join-Path $localAppData 'WinPool'

if (-not $dataRoot.Equals(
        [System.IO.Path]::GetFullPath($expectedDataRoot),
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected WinPool data path: $dataRoot"
}

function Assert-DescendantPath([string]$parentPath, [string]$childPath) {
    $relativePath = [System.IO.Path]::GetRelativePath($parentPath, $childPath)
    $parentPrefix = "..$([System.IO.Path]::DirectorySeparatorChar)"
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -eq '..' -or
        $relativePath.StartsWith($parentPrefix, [System.StringComparison]::Ordinal)) {
        throw "Path is outside the required parent '$parentPath': $childPath"
    }
}

function Stop-WinPoolProcesses {
    $processNames = @(
        'WinPool.App',
        'WinPool.Agent'
    )
    $running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Write-Output "Stopping $($running.Count) WinPool process(es)."
    $running | Stop-Process -Force

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    } while ($running.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($running.Count -gt 0) {
        throw "WinPool processes are still running: $($running.ProcessName -join ', ')."
    }
}

if (-not (Test-Path -LiteralPath $dataRoot -PathType Container)) {
    Write-Output "WinPool local data is already clean: $dataRoot"
    return
}

$dateFolder = '{0}_winpool_local_data_reset' -f (Get-Date -Format 'yyyyMMdd')
$runFolder = Get-Date -Format 'yyyyMMddTHHmmssfff'
$archiveRunRoot = [System.IO.Path]::GetFullPath(
    (Join-Path (Join-Path $rubbishRoot $dateFolder) $runFolder))
$archivePath = [System.IO.Path]::GetFullPath(
    (Join-Path $archiveRunRoot 'LocalAppData\WinPool'))

Assert-DescendantPath $projectRoot $rubbishRoot
Assert-DescendantPath $rubbishRoot $archivePath

if (Test-Path -LiteralPath $archivePath) {
    throw "Archive path already exists and will not be overwritten: $archivePath"
}

$sourceFiles = @(Get-ChildItem -LiteralPath $dataRoot -Force -Recurse -File)
$sourceFileCount = $sourceFiles.Count
$sourceByteCount = ($sourceFiles | Measure-Object -Property Length -Sum).Sum
if ($null -eq $sourceByteCount) {
    $sourceByteCount = 0
}

Write-Output "Source:  $dataRoot"
Write-Output "Archive: $archivePath"
Write-Output "Files:   $sourceFileCount"
Write-Output "Bytes:   $sourceByteCount"

$action = 'Stop WinPool processes and move the local data directory to the recoverable project Rubbish archive'
if (-not $PSCmdlet.ShouldProcess($dataRoot, $action)) {
    return
}

Stop-WinPoolProcesses

$archiveParent = Split-Path -Parent $archivePath
New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
Move-Item -LiteralPath $dataRoot -Destination $archivePath

if (Test-Path -LiteralPath $dataRoot) {
    throw "Reset verification failed because the source still exists: $dataRoot"
}
if (-not (Test-Path -LiteralPath $archivePath -PathType Container)) {
    throw "Reset verification failed because the archive is missing: $archivePath"
}

$archiveFiles = @(Get-ChildItem -LiteralPath $archivePath -Force -Recurse -File)
$archiveFileCount = $archiveFiles.Count
$archiveByteCount = ($archiveFiles | Measure-Object -Property Length -Sum).Sum
if ($null -eq $archiveByteCount) {
    $archiveByteCount = 0
}
if ($archiveFileCount -ne $sourceFileCount -or $archiveByteCount -ne $sourceByteCount) {
    throw "Reset verification failed: source was $sourceFileCount file(s)/$sourceByteCount byte(s), archive is $archiveFileCount file(s)/$archiveByteCount byte(s)."
}

Write-Output 'WinPool local data reset completed.'
Write-Output "Recoverable archive: $archivePath"
Write-Output 'The next WinPool launch will create a fresh current-schema data root.'
