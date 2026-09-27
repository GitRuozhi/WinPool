Set-StrictMode -Version Latest

function Assert-WinPoolCheckoutPath([string]$Path) {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $relative = [IO.Path]::GetRelativePath($repository, $fullPath)
    if ($relative -eq '.' -or $relative -eq '..' -or
        $relative.StartsWith('..\') -or $relative.StartsWith('../') -or
        [IO.Path]::IsPathRooted($relative)) {
        throw "Runtime path must be a child of the WinPool checkout: $fullPath"
    }
    $cursor = $repository
    foreach ($part in @('.') + @($relative -split '[\\/]')) {
        if ($part -ne '.') { $cursor = Join-Path $cursor $part }
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a runtime path through a link: $cursor"
        }
    }
    return $fullPath
}

function Get-WinPoolRecoveryRoot {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $parent = Split-Path -Parent $repository
    # The combined workspace keeps recovery files at its root. A standalone
    # checkout keeps them beside the checkout, never inside a runtime tree.
    $workspace = $parent
    for ($candidate = $repository; $candidate; $candidate = Split-Path -Parent $candidate) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'AGENTS.md')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'Program'))) {
            $workspace = $candidate
            break
        }
    }
    return Join-Path $workspace ('Rubbish/' + (Get-Date -Format yyyyMMdd) + '_winpool-build')
}

function Assert-WinPoolTreeHasNoLinks([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $items = @((Get-Item -LiteralPath $Path -Force)) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a runtime tree containing a link: $($item.FullName)"
        }
    }
}

function Move-WinPoolGeneratedOutput([string]$Path) {
    $fullPath = Assert-WinPoolCheckoutPath $Path
    if (-not (Test-Path -LiteralPath $fullPath)) { return $null }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
    $relative = [IO.Path]::GetRelativePath($repository, $fullPath)
    Assert-WinPoolTreeHasNoLinks $fullPath
    $recoveryRoot = [IO.Path]::GetFullPath((Get-WinPoolRecoveryRoot))
    $destination = Join-Path $recoveryRoot ([guid]::NewGuid().ToString('N') + '/Program/WinPool/' + $relative)
    $resolvedDestination = [IO.Path]::GetFullPath($destination)
    if (-not $resolvedDestination.StartsWith($recoveryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Recovery destination escaped its root: $resolvedDestination"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedDestination) -Force | Out-Null
    Move-Item -LiteralPath $fullPath -Destination $resolvedDestination -ErrorAction Stop
    if ((Test-Path -LiteralPath $fullPath) -or -not (Test-Path -LiteralPath $resolvedDestination)) {
        throw "Recovery move could not be verified: $fullPath -> $resolvedDestination"
    }
    return $resolvedDestination
}

function Assert-WinPoolRuntimeStopped([string]$RuntimeRoot) {
    $root = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($process in @(Get-Process -Name 'WinPool.App', 'WinPool.Agent' -ErrorAction SilentlyContinue)) {
        try { $imagePath = $process.Path } catch { throw "Cannot verify running WinPool process $($process.Id)." }
        if ([string]::IsNullOrWhiteSpace($imagePath)) { throw "Cannot verify running WinPool process $($process.Id)." }
        if ([IO.Path]::GetFullPath($imagePath).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Exit WinPool before replacing its runtime tree: PID $($process.Id), $imagePath"
        }
    }
}
