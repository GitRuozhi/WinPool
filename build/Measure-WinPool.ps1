<#
.SYNOPSIS
Reports the size of the tracked WinPool product source and tests.

.DESCRIPTION
Counts tracked C# and XAML files under src as product source and tracked C#
files under tests as test code. It reports physical lines, non-blank lines, and
file bytes. Git tracking is used as the input boundary, so ignored artifacts,
bin/obj output, databases, logs, and temporary files are not counted.

Use -Json for machine-readable console output or -JsonPath to save that output
to an explicitly selected file. No output file is created by default.
#>
[CmdletBinding()]
param(
    [switch]$Json,

    [string]$JsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-TrackedPaths([string]$relativeRoot) {
    $paths = @(git -C $repositoryRoot ls-files -- $relativeRoot)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate tracked files under '$relativeRoot'."
    }

    foreach ($relativePath in $paths) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            Get-Item -LiteralPath $fullPath
        }
    }
}

function Get-FileMetrics([System.IO.FileInfo]$file) {
    $lines = @(Get-Content -LiteralPath $file.FullName)
    $nonBlankLines = @($lines | Where-Object { $_.Trim().Length -gt 0 }).Count

    [pscustomobject]@{
        File = $file
        PhysicalLines = $lines.Count
        NonBlankLines = $nonBlankLines
        Bytes = [long]$file.Length
    }
}

function Get-GroupMetrics(
    [string]$name,
    [System.IO.FileInfo[]]$files) {
    $fileMetrics = @($files | ForEach-Object { Get-FileMetrics $_ })
    $physicalLines = [long](($fileMetrics | Measure-Object -Property PhysicalLines -Sum).Sum)
    $nonBlankLines = [long](($fileMetrics | Measure-Object -Property NonBlankLines -Sum).Sum)
    $bytes = [long](($fileMetrics | Measure-Object -Property Bytes -Sum).Sum)

    [pscustomobject]@{
        Group = $name
        Files = $fileMetrics.Count
        PhysicalLines = $physicalLines
        NonBlankLines = $nonBlankLines
        Bytes = $bytes
        MiB = [math]::Round($bytes / 1MB, 2)
    }
}

function Get-ProjectMetrics(
    [string]$relativeRoot,
    [string[]]$extensions) {
    $files = @(Get-TrackedPaths $relativeRoot | Where-Object {
        $extensions -contains $_.Extension.ToLowerInvariant()
    })

    $groups = $files | Group-Object {
        $relativePath = $_.FullName.Substring($repositoryRoot.Length + 1)
        $parts = $relativePath -split '[\\/]'
        if ($parts.Count -ge 2) {
            $parts[1]
        }
        else {
            $relativeRoot
        }
    }

    foreach ($group in $groups | Sort-Object Name) {
        Get-GroupMetrics $group.Name ([System.IO.FileInfo[]]$group.Group)
    }
}

$productFiles = @(Get-TrackedPaths 'src' | Where-Object {
    $_.Extension.ToLowerInvariant() -in @('.cs', '.xaml')
})
$testFiles = @(Get-TrackedPaths 'tests' | Where-Object {
    $_.Extension.ToLowerInvariant() -eq '.cs'
})

$summary = @(
    Get-GroupMetrics 'Product source (src: C# + XAML)' ([System.IO.FileInfo[]]$productFiles)
    Get-GroupMetrics 'Tests (tests: C#)' ([System.IO.FileInfo[]]$testFiles)
    Get-GroupMetrics 'Product source + tests' ([System.IO.FileInfo[]]($productFiles + $testFiles))
)

$projects = @(
    Get-ProjectMetrics 'src' @('.cs', '.xaml')
    Get-ProjectMetrics 'tests' @('.cs')
)

$report = [pscustomobject]@{
    Repository = $repositoryRoot
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('O')
    Summary = $summary
    Projects = $projects
}

if ($JsonPath) {
    $jsonTarget = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $JsonPath))
    $jsonParent = Split-Path -Parent $jsonTarget
    if (-not (Test-Path -LiteralPath $jsonParent -PathType Container)) {
        New-Item -ItemType Directory -Path $jsonParent -Force | Out-Null
    }

    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonTarget -Encoding utf8
    Write-Output "Wrote $jsonTarget"
}

if ($Json) {
    $report | ConvertTo-Json -Depth 5
}
else {
    $summary | Format-Table Group, Files, PhysicalLines, NonBlankLines, Bytes, MiB -AutoSize
    Write-Output ''
    Write-Output 'By project:'
    $projects | Format-Table Group, Files, PhysicalLines, NonBlankLines, Bytes, MiB -AutoSize
}
