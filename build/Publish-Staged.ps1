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

New-Item -ItemType Directory -Path $stageRoot | Out-Null

$publishProjects = @(
    (Join-Path $repositoryRoot 'src\WinPool.App\WinPool.App.csproj'),
    (Join-Path $repositoryRoot 'src\WinPool.Agent\WinPool.Agent.csproj')
)

foreach ($project in $publishProjects) {
    & dotnet restore $project --runtime win-x64 '-p:Platform=x64'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for $project with exit code $LASTEXITCODE. Partial staging was retained at $stageRoot."
    }
}

& dotnet publish (Join-Path $repositoryRoot 'src\WinPool.App\WinPool.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $stageRoot `
    '-p:Platform=x64'

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Partial staging was retained at $stageRoot."
}

Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Filter '*.pdb' |
    Remove-Item -Force

$requiredExecutables = @{
    'WinPool.App.exe' = 'WinPool.App.exe'
    'WinPool.Agent.exe' = 'Agent/WinPool.Agent.exe'
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

Write-Output "Verified $expectedProductVersion staging tree: $stageRoot"
foreach ($entry in $requiredExecutables.GetEnumerator() | Sort-Object Value) {
    Write-Output "  $($entry.Value)"
}
