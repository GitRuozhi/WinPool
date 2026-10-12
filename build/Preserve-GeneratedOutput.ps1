Set-StrictMode -Version Latest

if (-not ('WinPoolBuildProcessProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class WinPoolBuildProcessProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
        StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool HasExited(IntPtr process)
    {
        uint code;
        if (!GetExitCodeProcess(process, out code))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return code != 259; // STILL_ACTIVE; an ambiguous state remains blocked.
    }

    public static string GetRunningImagePath(int processId)
    {
        // Unlike Process.Path/MainModule, this does not request module-memory
        // access. The same handle binds the image and exit-state observations.
        IntPtr process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87 && processId > 0) return null; // PID no longer exists.
            throw new Win32Exception(error);
        }
        try
        {
            if (HasExited(process)) return null;
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length))
            {
                int error = Marshal.GetLastWin32Error();
                if (HasExited(process)) return null;
                throw new Win32Exception(error);
            }
            if (HasExited(process)) return null;
            if (length == 0) throw new InvalidOperationException("Running process returned an empty image path.");
            return path.ToString();
        }
        finally { CloseHandle(process); }
    }
}
'@
}

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
        try { $imagePath = [WinPoolBuildProcessProbe]::GetRunningImagePath($process.Id) }
        catch {
            throw "Cannot verify running WinPool process $($process.Id): $($_.Exception.GetBaseException().Message) Exit WinPool from its tray icon before rebuilding; if access is denied, use an elevated PowerShell. No runtime replacement was allowed."
        }
        if ($null -eq $imagePath) { continue } # Positively confirmed exited/missing.
        if ([IO.Path]::GetFullPath($imagePath).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Exit WinPool from its tray icon before replacing its runtime tree: PID $($process.Id), $imagePath. Closing only the App window leaves the Agent running."
        }
    }
}
