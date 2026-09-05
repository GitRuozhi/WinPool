[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipDesktopShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'WinPool.slnx'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$runRoot = Join-Path $artifactsRoot $Configuration
$appExe = Join-Path $runRoot 'WinPool.App.exe'

$requiredExecutables = [ordered]@{
    'WinPool.App.exe' = 'WinPool.App.exe'
    'WinPool.Agent.exe' = 'WinPool.Agent.exe'
}

function Invoke-Native([string]$fileName, [string[]]$arguments) {
    & $fileName @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$fileName $($arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-RunTree {
    foreach ($entry in $requiredExecutables.GetEnumerator()) {
        $path = Join-Path $runRoot $entry.Value
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing $($entry.Key) at $path"
        }
    }
}

function New-WinPoolShortcut([string]$shortcutPath, [string]$targetPath) {
    $folder = Split-Path -Parent $shortcutPath
    if (-not (Test-Path -LiteralPath $folder)) {
        New-Item -ItemType Directory -Path $folder | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetPath
    $shortcut.WorkingDirectory = (Split-Path -Parent $targetPath)
    $shortcut.WindowStyle = 1
    $shortcut.Description = 'WinPool'
    $shortcut.IconLocation = "$targetPath,0"
    $shortcut.Save()
    Write-Output "Shortcut: $shortcutPath"
}

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "WinPool solution was not found: $solutionPath"
}

Set-Location -LiteralPath $repositoryRoot
& (Join-Path $PSScriptRoot 'Clean-WinPool.ps1')

Invoke-Native 'dotnet' @('restore', $solutionPath)
Invoke-Native 'dotnet' @(
    'build', $solutionPath,
    '-c', $Configuration,
    '--no-restore',
    '-m:1'
)

Assert-RunTree

$repoShortcut = Join-Path $repositoryRoot 'WinPool.lnk'
New-WinPoolShortcut $repoShortcut $appExe

if (-not $SkipDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($desktop)) {
        throw 'The current-user Desktop folder is unavailable.'
    }
    New-WinPoolShortcut (Join-Path $desktop 'WinPool.lnk') $appExe
}

Write-Output "Rebuilt $Configuration tree: $runRoot"
foreach ($entry in $requiredExecutables.GetEnumerator()) {
    Write-Output "  $($entry.Value)"
}
