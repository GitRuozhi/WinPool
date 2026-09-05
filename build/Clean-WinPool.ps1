<#
.SYNOPSIS
Removes WinPool generated output without restoring or building.

.DESCRIPTION
Stops running WinPool processes (their executables live in the artifacts run
tree), then removes the regenerable artifacts tree, leftover bin/obj project
folders, and the generated repository-root shortcut. No restore, build, or
shortcut creation runs; use Rebuild-WinPool.ps1 afterwards to rebuild.

Use -WhatIf to preview what would be removed without changing state.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$repoShortcut = Join-Path $repositoryRoot 'WinPool.lnk'

function Stop-WinPoolProcesses {
    $names = @('WinPool.App', 'WinPool.Agent')
    $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    $propertiesHosts = @(Get-CimInstance Win32_Process -Filter "Name='rundll32.exe'" |
        Where-Object { $_.CommandLine -like '*DeviceProperties_RunDLL*' })
    if ($running.Count -eq 0 -and $propertiesHosts.Count -eq 0) {
        return
    }

    if ($running.Count -gt 0) {
        Write-Output "Stopping $($running.Count) WinPool process(es)."
        $running | Stop-Process -Force
    }
    if ($propertiesHosts.Count -gt 0) {
        Write-Output "Stopping $($propertiesHosts.Count) leftover device-properties host(s)."
        $propertiesHosts | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
        $propertiesHosts = @(Get-CimInstance Win32_Process -Filter "Name='rundll32.exe'" |
            Where-Object { $_.CommandLine -like '*DeviceProperties_RunDLL*' })
    } while (($running.Count -gt 0 -or $propertiesHosts.Count -gt 0) -and [datetime]::UtcNow -lt $deadline)

    if ($running.Count -gt 0) {
        throw "WinPool processes are still running: $($running.ProcessName -join ', ')."
    }
    if ($propertiesHosts.Count -gt 0) {
        throw "Device-properties hosts are still running: $($propertiesHosts.ProcessId -join ', ')."
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

$removedSomething = $false

if ($PSCmdlet.ShouldProcess('WinPool processes', 'Stop WinPool processes and leftover device-properties hosts')) {
    Stop-WinPoolProcesses
}

if (Test-Path -LiteralPath $artifactsRoot) {
    if ($PSCmdlet.ShouldProcess($artifactsRoot, 'Remove regenerable output')) {
        $removedSomething = $true
        Write-Output "Removing regenerable output under $artifactsRoot"
        Remove-DirectoryTree $artifactsRoot
    }
}
else {
    Write-Output "No artifacts tree exists under $artifactsRoot"
}

$leftovers = @(Get-ProjectOutputDirectories)
foreach ($directory in $leftovers) {
    if ($PSCmdlet.ShouldProcess($directory.FullName, 'Remove leftover project output')) {
        $removedSomething = $true
        Write-Output "Removing leftover $($directory.FullName.Substring($repositoryRoot.Length + 1))"
        Remove-DirectoryTree $directory.FullName
    }
}

if ($leftovers.Count -eq 0) {
    Write-Output 'No leftover bin/obj project folders found.'
}

if (Test-Path -LiteralPath $repoShortcut) {
    if ($PSCmdlet.ShouldProcess($repoShortcut, 'Remove the generated repository shortcut')) {
        $removedSomething = $true
        Write-Output "Removing generated shortcut $repoShortcut"
        Remove-Item -LiteralPath $repoShortcut -Force
    }
}

if (-not $WhatIfPreference) {
    if (-not $removedSomething) {
        Write-Output 'Nothing to clean; the workspace has no generated output.'
    }

    Write-Output 'Clean completed. Run Rebuild-WinPool.ps1 to rebuild.'
    Write-Output 'Note: a Desktop shortcut keeps pointing at the removed run tree until the next rebuild.'
}
