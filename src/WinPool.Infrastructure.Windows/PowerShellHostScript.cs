using System.Text;

namespace WinPool.Infrastructure.Windows;

internal static class PowerShellHostScript
{
    // One fixed loader. The first bounded frame contains only assembly-owned templates.
    public static string EncodedBootstrap { get; } = Convert.ToBase64String(Encoding.Unicode.GetBytes(Source));
    private const string Source = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
$inputStream = [Console]::OpenStandardInput()
$outputStream = [Console]::OpenStandardOutput()
function Read-HostFrame {
    $header = [byte[]]::new(4)
    $offset = 0
    while ($offset -lt 4) {
        $count = $inputStream.Read($header,$offset,4-$offset)
        if ($count -eq 0) { if ($offset -eq 0) { return $null }; throw 'incomplete-host-header' }
        $offset += $count
    }
    $length = [BitConverter]::ToInt32($header,0)
    if ($length -le 0 -or $length -gt 4194304) { throw 'invalid-host-request-size' }
    $body = [byte[]]::new($length)
    $offset = 0
    while ($offset -lt $length) {
        $count = $inputStream.Read($body,$offset,$length-$offset)
        if ($count -eq 0) { throw 'incomplete-host-body' }
        $offset += $count
    }
    return ([Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json -ErrorAction Stop)
}
function Write-HostFrame($frame) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($frame | ConvertTo-Json -Depth 12 -Compress))
    $header = [BitConverter]::GetBytes([int]$bytes.Length)
    $outputStream.Write($header,0,4)
    $outputStream.Write($bytes,0,$bytes.Length)
    $outputStream.Flush()
}
try {
    $initial = Read-HostFrame
    if ($null -eq $initial -or $initial.Protocol -ne 1) { throw 'invalid-host-initialization' }
    $hostIncarnation = [string]$initial.Incarnation
    $inventoryScript = [ScriptBlock]::Create([string]$initial.Inventory)
    $writeScript = [ScriptBlock]::Create([string]$initial.Write)
    $queryScript = [ScriptBlock]::Create([string]$initial.Query)
    if ($initial.ImportStorage) { Import-Module Storage -ErrorAction Stop }
    $initial = $null
    Write-HostFrame @{ Kind='ready'; Protocol=1; Incarnation=$hostIncarnation }
    while ($true) {
        $request = Read-HostFrame
        if ($null -eq $request) { break }
        if ([string]$request.Incarnation -ne $hostIncarnation -or [string]::IsNullOrWhiteSpace([string]$request.RequestId)) { throw 'invalid-host-request-identity' }
        $kind = [string]$request.Kind
        Write-HostFrame @{ Kind='started'; Incarnation=$hostIncarnation; RequestId=[string]$request.RequestId }
        $success = $true
        try {
            if ($kind -eq 'Inventory') { $resultJson = & $inventoryScript $request.Parameters }
            elseif ($kind -eq 'Write') { $resultJson = & $writeScript $request.Parameters }
            elseif ($kind -in @('PartitionSize','VirtualDiskSize','VirtualDiskProbe','TierSize','TierCapability','VolumeFormat','StorageJobs','BitLocker','BitLockerCandidates','PhysicalDevice','NativeMsrAttributes','MachineIdentity')) {
                $resultJson = & $queryScript $kind $request.Parameters
            }
            else { throw 'closed-host-command-required' }
            if ($resultJson -isnot [string]) { throw 'fixed-template-returned-unstructured-output' }
            if ([Text.Encoding]::UTF8.GetByteCount($resultJson) -gt 67108864) { throw 'raw-host-result-exceeds-64MiB' }
        }
        catch { $success=$false; $resultJson=[string]$_.Exception.Message }
        Write-HostFrame @{ Kind='result'; Incarnation=$hostIncarnation; RequestId=[string]$request.RequestId; Success=$success; ResultJson=$resultJson }
        $request=$null; $resultJson=$null
    }
}
catch { [Console]::Error.WriteLine([string]$_); exit 1 }
""";
}
