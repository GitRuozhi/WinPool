[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stageRoot = [System.IO.Path]::GetFullPath($OutputPath)
[xml]$versionProps = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$versionGroup = @($versionProps.Project.PropertyGroup) |
    Where-Object { $null -ne $_.WinPoolVersionMajor } |
    Select-Object -First 1
$iteration = [int]$versionGroup.WinPoolVersionIteration
$expectedProductVersion = if ($iteration -eq 0) {
    "V$($versionGroup.WinPoolVersionMajor).$($versionGroup.WinPoolVersionMinor)"
}
else {
    "V$($versionGroup.WinPoolVersionMajor).$($versionGroup.WinPoolVersionMinor)$iteration"
}

if (Test-Path -LiteralPath $stageRoot) {
    throw "Staging path already exists and will not be overwritten: $stageRoot"
}

function Get-PublishFileMap([string]$root) {
    $map = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File)) {
        if ($file.Extension -eq '.pdb') {
            continue
        }

        $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if ($map.ContainsKey($relative)) {
            throw "Duplicate relative path in publish tree ${root}: $relative"
        }

        $map[$relative] = $file.FullName
    }

    return $map
}

function Copy-StagedFile([string]$sourcePath, [string]$destinationPath) {
    $directory = Split-Path -Parent $destinationPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

function Merge-PublishTrees([string]$appPublish, [string]$agentPublish, [string]$destination) {
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
        $destinationPath = Join-Path $destination ($relative.Replace('/', '\'))

        if ($inApp -and $inAgent) {
            $appHash = (Get-FileHash -LiteralPath $appMap[$relative] -Algorithm SHA256).Hash
            $agentHash = (Get-FileHash -LiteralPath $agentMap[$relative] -Algorithm SHA256).Hash
            if ($appHash -ne $agentHash) {
                throw "Staging collision: $relative has different SHA-256 hashes (App=$appHash, Agent=$agentHash)."
            }

            Copy-StagedFile $appMap[$relative] $destinationPath
            $sharedCount += 1
        }
        elseif ($inApp) {
            Copy-StagedFile $appMap[$relative] $destinationPath
            $appOnlyCount += 1
        }
        else {
            Copy-StagedFile $agentMap[$relative] $destinationPath
            $agentOnlyCount += 1
        }
    }

    Write-Output "Union merge: $sharedCount shared, $appOnlyCount App-only, $agentOnlyCount Agent-only, 0 collisions"
}

$appProject = Join-Path $repositoryRoot 'src\WinPool.App\WinPool.App.csproj'
$agentProject = Join-Path $repositoryRoot 'src\WinPool.Agent\WinPool.Agent.csproj'
$publishProjects = @($appProject, $agentProject)
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('winpool-stage-' + [guid]::NewGuid().ToString('N'))
$appPublish = Join-Path $tempRoot 'App'
$agentPublish = Join-Path $tempRoot 'Agent'

New-Item -ItemType Directory -Path $stageRoot | Out-Null
New-Item -ItemType Directory -Path $appPublish | Out-Null
New-Item -ItemType Directory -Path $agentPublish | Out-Null

try {
    foreach ($project in $publishProjects) {
        & dotnet restore $project --runtime win-x64 '-p:Platform=x64'
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for $project with exit code $LASTEXITCODE. Partial staging was retained at $stageRoot."
        }
    }

    & dotnet publish $appProject `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $appPublish `
        '-p:Platform=x64' `
        '-p:PublishTrimmed=false'

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for WinPool.App with exit code $LASTEXITCODE. Partial staging was retained at $stageRoot."
    }

    & dotnet publish $agentProject `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $agentPublish `
        '-p:Platform=x64' `
        '-p:PublishTrimmed=false'

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for WinPool.Agent with exit code $LASTEXITCODE. Partial staging was retained at $stageRoot."
    }

    Merge-PublishTrees $appPublish $agentPublish $stageRoot
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$requiredExecutables = @{
    'WinPool.App.exe' = 'WinPool.App.exe'
    'WinPool.Agent.exe' = 'WinPool.Agent.exe'
}

$allFiles = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File)
foreach ($entry in $requiredExecutables.GetEnumerator()) {
    $matches = @($allFiles | Where-Object Name -eq $entry.Key)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $($entry.Key); found $($matches.Count). The staging tree contains duplicate or missing process executables."
    }

    $actualRelativePath = [System.IO.Path]::GetRelativePath($stageRoot, $matches[0].FullName).Replace('\', '/')
    if ($actualRelativePath -ne $entry.Value) {
        throw "Expected $($entry.Key) at $($entry.Value), found $actualRelativePath."
    }

    $actualProductVersion = $matches[0].VersionInfo.ProductVersion
    if ($actualProductVersion -ne $expectedProductVersion) {
        throw "Expected $($entry.Key) product version $expectedProductVersion, found $actualProductVersion."
    }
}

$forbiddenFilePatterns = @('*.ps1', '*.pdb', '*.db', '*.db-wal', '*.db-shm', '*.trx', '*.log')
$forbiddenFiles = @(
    $allFiles | Where-Object {
        $name = $_.Name
        @($forbiddenFilePatterns | Where-Object { $name -like $_ }).Count -gt 0
    })
if ($forbiddenFiles.Count -gt 0) {
    $relativePaths = $forbiddenFiles |
        ForEach-Object { [System.IO.Path]::GetRelativePath($stageRoot, $_.FullName) } |
        Sort-Object
    throw "Staging contains forbidden files: $($relativePaths -join ', ')"
}

$forbiddenDirectories = @('local-assets', 'TestResults')
$presentForbiddenDirectories = @(
    Get-ChildItem -LiteralPath $stageRoot -Recurse -Directory |
        Where-Object { $forbiddenDirectories -contains $_.Name })
if ($presentForbiddenDirectories.Count -gt 0) {
    $relativePaths = $presentForbiddenDirectories |
        ForEach-Object { [System.IO.Path]::GetRelativePath($stageRoot, $_.FullName) } |
        Sort-Object
    throw "Staging contains forbidden directories: $($relativePaths -join ', ')"
}

$externalToolNames = @('diskspd.exe', 'fio.exe', 'robocopy.exe', 'rammap.exe', 'rammap64.exe', 'Dite.exe')
$bundledExternalTools = @($allFiles | Where-Object { $externalToolNames -contains $_.Name })
if ($bundledExternalTools.Count -gt 0) {
    $relativePaths = $bundledExternalTools |
        ForEach-Object { [System.IO.Path]::GetRelativePath($stageRoot, $_.FullName) } |
        Sort-Object
    throw "Staging bundles forbidden external tools: $($relativePaths -join ', ')"
}

$bytes = ($allFiles | Measure-Object -Property Length -Sum).Sum
$mebibytes = [math]::Round($bytes / 1MB, 2)
Write-Output "Verified $expectedProductVersion staging tree: $stageRoot"
Write-Output "  $($allFiles.Count) files, $mebibytes MiB"
foreach ($entry in $requiredExecutables.GetEnumerator() | Sort-Object Value) {
    Write-Output "  $($entry.Value)"
}
