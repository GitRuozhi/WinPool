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
    'WinPool.Agent.exe' = 'Agent\WinPool.Agent.exe'
    'WinPool.TestWorker.exe' = 'Agent\TestWorker\WinPool.TestWorker.exe'
    'WinPool.ElevatedBroker.exe' = 'Agent\Broker\WinPool.ElevatedBroker.exe'
}

function Invoke-Native([string]$fileName, [string[]]$arguments) {
    & $fileName @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$fileName $($arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Stop-WinPoolProcesses {
    $names = @('WinPool.App', 'WinPool.Agent', 'WinPool.TestWorker', 'WinPool.ElevatedBroker')
    $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Write-Output "Stopping $($running.Count) WinPool process(es)."
    $running | Stop-Process -Force
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    } while ($running.Count -gt 0 -and [datetime]::UtcNow -lt $deadline)

    if ($running.Count -gt 0) {
        throw "WinPool processes are still running: $($running.ProcessName -join ', ')."
    }
}

function Remove-DirectoryTree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $attempt = 0
    while ($true) {
        try {
            Remove-Item -LiteralPath $path -Recurse -Force
            return
        }
        catch {
            $attempt += 1
            if ($attempt -ge 8) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Get-ProjectOutputDirectories {
    $areas = @(
        (Join-Path $repositoryRoot 'src'),
        (Join-Path $repositoryRoot 'workers'),
        (Join-Path $repositoryRoot 'tests')
    )
    foreach ($area in $areas) {
        if (-not (Test-Path -LiteralPath $area)) {
            continue
        }

        Get-ChildItem -LiteralPath $area -Directory -Recurse |
            Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
            Where-Object {
                @(Get-ChildItem -LiteralPath $_.Parent.FullName -Filter '*.csproj' -File).Count -gt 0
            }
    }
}

function Clear-GeneratedOutput {
    Write-Output "Removing regenerable output under $artifactsRoot"
    Remove-DirectoryTree $artifactsRoot

    $leftovers = @(Get-ProjectOutputDirectories)
    foreach ($directory in $leftovers) {
        Write-Output "Removing leftover $($directory.FullName.Substring($repositoryRoot.Length + 1))"
        Remove-DirectoryTree $directory.FullName
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
Stop-WinPoolProcesses
Clear-GeneratedOutput

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
