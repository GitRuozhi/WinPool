<#
.SYNOPSIS
Repairs a Low mandatory integrity label that stops built WinPool programs from starting.

.DESCRIPTION
An inheritable Low label can prevent the built program from writing Medium
locations such as %LOCALAPPDATA%. This is separate from executable signing and
SmartScreen reputation; repairing a label does not sign the executable.

This script reports the label on the project root that contains Program\WinPool and on
the WinPool App and Agent executables, and resets the whole tree to Medium when any of
them is Low. Writing a mandatory label is a SACL write that needs WRITE_OWNER, so the
repair relaunches through UAC; the check itself needs no administrator rights.

Repairing the root also covers later builds, because new files inherit the root label.

.PARAMETER Path
Directory to inspect and repair. Defaults to the project root above Program\WinPool.

.PARAMETER CheckOnly
Report the current labels and exit without changing anything.

.PARAMETER LogPath
File that receives the run log. Defaults to a timestamped file under %TEMP%.

.NOTES
Exit codes: 0 normal or repaired, 1 repair failed, 2 elevation refused, 3 Low detected
with -CheckOnly. Close running WinPool programs before starting them again afterwards.

.EXAMPLE
.\Repair-WinPoolIntegrityLabel.ps1 -CheckOnly

.EXAMPLE
.\Repair-WinPoolIntegrityLabel.ps1
#>
[CmdletBinding()]
param(
    [string]$Path,
    [switch]$CheckOnly,
    [switch]$Elevated,
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
$Path = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "Target directory does not exist: $Path"
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path ([IO.Path]::GetTempPath()) ('winpool-integrity-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
}

$notes = [Collections.Generic.List[string]]::new()
function Add-Note([string]$message) {
    $notes.Add($message)
    Write-Host $message
}

if (-not ('WinPoolIntegrityLabel' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinPoolIntegrityLabel
{
    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x10;
    private const int InheritedAce = 0x10;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfoW(
        string name, int type, uint info,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAce(IntPtr sacl, uint index, out IntPtr ace);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public static string Read(string path)
    {
        IntPtr owner, group, dacl, sacl, sd;
        uint result = GetNamedSecurityInfoW(
            path, SeFileObject, LabelSecurityInformation,
            out owner, out group, out dacl, out sacl, out sd);
        if (result != 0)
        {
            return "error:" + result;
        }

        try
        {
            if (sacl == IntPtr.Zero)
            {
                return "Default";
            }

            IntPtr ace;
            if (!GetAce(sacl, 0, out ace))
            {
                return "Default";
            }

            IntPtr sid = new IntPtr(ace.ToInt64() + 8);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            bool inherited = (Marshal.ReadByte(ace, 1) & InheritedAce) != 0;

            string name;
            switch (rid)
            {
                case 0x0000: name = "Untrusted"; break;
                case 0x1000: name = "Low"; break;
                case 0x2000: name = "Medium"; break;
                case 0x2100: name = "MediumPlus"; break;
                case 0x3000: name = "High"; break;
                case 0x4000: name = "System"; break;
                default: name = "0x" + rid.ToString("X4"); break;
            }

            return inherited ? name + " (inherited)" : name;
        }
        finally
        {
            LocalFree(sd);
        }
    }
}
'@
}

function Test-LowLabel([string]$label) {
    return $label -like 'Low*' -or $label -like 'Untrusted*'
}

function Complete([int]$code) {
    try {
        $notes | Set-Content -LiteralPath $LogPath -Encoding UTF8
        Write-Host "Log: $LogPath"
    }
    catch {
        Write-Host "Could not write the log: $($_.Exception.Message)"
    }
    exit $code
}

$targets = [Collections.Generic.List[string]]::new()
$targets.Add($Path)
$runtime = if (Test-Path -LiteralPath (Join-Path $Path 'WinPool.slnx')) { Join-Path $Path 'artifacts\Release' }
    elseif (Test-Path -LiteralPath (Join-Path $Path 'Program\WinPool\WinPool.slnx')) { Join-Path $Path 'Program\WinPool\artifacts\Release' }
    elseif (Test-Path -LiteralPath (Join-Path $Path 'WinPool.App.exe')) { $Path }
    else { throw "The target is not a WinPool checkout, research layout, or runtime: $Path" }
foreach ($relative in @('WinPool.App.exe','WinPool.Agent.exe')) {
    $candidate = Join-Path $runtime $relative
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $targets.Add($candidate) }
}

$labels = [ordered]@{}
foreach ($target in $targets) {
    $labels[$target] = [WinPoolIntegrityLabel]::Read($target)
    if ($labels[$target] -like 'error:*') { throw "Cannot read integrity label on '$target': $($labels[$target])" }
}

Add-Note "Integrity labels under '$Path':"
foreach ($target in $targets) {
    Add-Note ('  {0,-18} {1}' -f $labels[$target], $target)
}

$lowTargets = @($targets | Where-Object { Test-LowLabel $labels[$_] })
if ($lowTargets.Count -eq 0) {
    Add-Note 'All labels are Medium or higher; nothing to repair.'
    Complete 0
}

Add-Note "$($lowTargets.Count) target(s) run at Low integrity and must be reset."

if ($CheckOnly) {
    Add-Note 'CheckOnly: no change was made. Run without -CheckOnly to repair.'
    Complete 3
}

if (-not $Elevated) {
    Add-Note 'A mandatory label lives in the SACL, so resetting it needs administrator rights.'
    Add-Note 'Accept the following UAC prompt to continue.'
    $hostExe = (Get-Process -Id $PID).Path
    # Quoted explicitly: Start-Process joins the array into one command line.
    $argumentLine = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Path "{1}" -Elevated -LogPath "{2}"' -f @(
        $PSCommandPath, $Path, $LogPath)
    try {
        $process = Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argumentLine -Wait -PassThru -ErrorAction Stop
    }
    catch {
        Write-Host "Elevation was refused or failed: $($_.Exception.Message)"
        exit 2
    }
    if (Test-Path -LiteralPath $LogPath) {
        Get-Content -LiteralPath $LogPath | ForEach-Object { Write-Host $_ }
    }
    exit $process.ExitCode
}

Add-Note 'Resetting the mandatory label to Medium for the whole tree; this can take minutes.'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$raw = & icacls $Path /setintegritylevel '(OI)(CI)Medium' /T /C /Q 2>&1
$exitCode = $LASTEXITCODE
$stopwatch.Stop()
$raw | Select-Object -Last 3 | ForEach-Object { Add-Note "icacls: $_" }
Add-Note ("icacls exited {0} after {1:N1}s" -f $exitCode, $stopwatch.Elapsed.TotalSeconds)
if ($exitCode -ne 0) { Add-Note "Integrity repair failed on '$Path'."; Complete 1 }

$after = [ordered]@{}
foreach ($target in $targets) {
    $after[$target] = [WinPoolIntegrityLabel]::Read($target)
    if ($after[$target] -like 'error:*') { Add-Note "Cannot verify integrity label on '$target': $($after[$target])"; Complete 1 }
}
foreach ($target in $targets) {
    Add-Note ('  {0,-18} {1}' -f $after[$target], $target)
}

$stillLow = @($targets | Where-Object { Test-LowLabel $after[$_] })
if ($stillLow.Count -gt 0) {
    Add-Note 'Repair incomplete: some targets are still Low.'
    Complete 1
}

Add-Note 'Repaired. Close running WinPool programs and start them again.'
Complete 0
