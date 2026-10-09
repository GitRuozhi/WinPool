using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class EmbeddedStorageInventoryTierMemberTests
{
    [Fact]
    public async Task ExactSingleHddTierUsesAllocatedPhysicalAssociationWithoutExtentEnumeration()
    {
        using var result = await RunAsync("fast");

        Assert.Equal("uid:physical-uid", Assert.Single(ReadKeys(result)));
        Assert.Equal(0, result.RootElement.GetProperty("ExtentCalls").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("AllocationQueries").GetInt32());
    }

    [Fact]
    public async Task ReturnedNullVirtualAggregateLayoutFieldsAllowExactTierLayout()
    {
        using var result = await RunAsync("virtual-layout-fields-null");

        Assert.Equal("uid:physical-uid", Assert.Single(ReadKeys(result)));
        Assert.Equal(0, result.RootElement.GetProperty("ExtentCalls").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("AllocationQueries").GetInt32());
    }

    [Theory]
    [InlineData("allocation-mismatch")]
    [InlineData("allocation-query-throws")]
    [InlineData("tier-size-mismatch")]
    [InlineData("write-cache-nonzero")]
    [InlineData("layout-bool")]
    [InlineData("virtual-layout-fields-incompatible")]
    [InlineData("virtual-layout-field-missing")]
    public async Task UnprovenAllocationLayoutFallsBackToExactExtentAssociation(string scenario)
    {
        using var result = await RunAsync(scenario);

        Assert.Equal("uid:physical-uid", Assert.Single(ReadKeys(result)));
        Assert.Equal(1, result.RootElement.GetProperty("ExtentCalls").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("AllocationQueries").GetInt32());
        Assert.Empty(result.RootElement.GetProperty("SourceQueryFailures").EnumerateArray());
    }

    [Fact]
    public async Task MultipleActualTiersKeepStrictExtentAssociationPath()
    {
        using var result = await RunAsync("multiple-tiers");

        Assert.Equal("uid:physical-uid", Assert.Single(ReadKeys(result)));
        Assert.Equal(1, result.RootElement.GetProperty("ExtentCalls").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("AllocationQueries").GetInt32());
    }

    [Fact]
    public async Task AmbiguousPoolMemberSetFallsBackToExactExtentAssociation()
    {
        using var result = await RunAsync("ambiguous-pool-members");

        Assert.Equal("uid:physical-uid", Assert.Single(ReadKeys(result)));
        Assert.Equal(1, result.RootElement.GetProperty("ExtentCalls").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("AllocationQueries").GetInt32());
    }

    [Fact]
    public void ProductionCollectorUsesOnlyAllocatedPhysicalDisksForTheSingleTierFastPath()
    {
        var script = EmbeddedStorageInventoryScript.ForPurpose(WinPool.Application.CollectionPurpose.Storage);

        Assert.Contains("Get-PhysicalDisk -VirtualDisk $virtual -HasAllocations $true -ErrorAction Stop", script);
        Assert.Contains("MemberPhysicalDiskKeys = @(Get-ExactTierMemberKeys $tier $virtual $pool $virtualTiers)", script);
        Assert.Contains("Get-ExactTierMemberKeysByExtent $tier $virtual", script);
        Assert.DoesNotContain("Get-PhysicalDisk -VirtualDisk $virtual -ErrorAction Stop", script);
    }

    private static string[] ReadKeys(JsonDocument result) => result.RootElement.GetProperty("Keys")
        .EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static async Task<JsonDocument> RunAsync(string scenario)
    {
        var harness = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$fixture = [Console]::In.ReadToEnd() | ConvertFrom-Json
$script:extentCalls = 0
$script:allocationQueries = 0
$sourceQueryFailures = [System.Collections.Generic.List[object]]::new()
$physical = [pscustomobject]@{ UniqueId='physical-uid'; ObjectId='physical-object'; MediaType='HDD' }
$physicalObjects = @($physical)
$pool = [pscustomobject]@{ UniqueId='pool-uid'; ObjectId='pool-object'; IsPrimordial=$false }
$script:poolParents = @($pool)
$script:poolMembers = @($physical)
$script:allocatedMembers = @($physical)
$virtual = [pscustomobject]@{
    UniqueId='virtual-uid'; ObjectId='virtual-object'; Size=[uint64]100; AllocatedSize=[uint64]100;
    FootprintOnPool=[uint64]100; WriteCacheSize=[uint64]0; ResiliencySettingName='Simple'; ProvisioningType='Fixed';
    NumberOfColumns=[uint16]1; Interleave=[uint64]65536; NumberOfDataCopies=[uint16]1; PhysicalDiskRedundancy=[uint16]0
}
$tier = [pscustomobject]@{
    UniqueId='tier-uid'; ObjectId='tier-object'; MediaType='HDD'; ResiliencySettingName='Simple'; ProvisioningType='Fixed';
    NumberOfColumns=[uint16]1; Interleave=[uint64]65536; NumberOfDataCopies=[uint16]1; PhysicalDiskRedundancy=[uint16]0;
    Size=[uint64]100; AllocatedSize=[uint64]100; FootprintOnPool=[uint64]100
}
$virtualTiers = @($tier)
switch ([string]$fixture.Scenario) {
    'allocation-mismatch' { $script:allocatedMembers = @([pscustomobject]@{ UniqueId='other-uid'; ObjectId='other-object'; MediaType='HDD' }) }
    'tier-size-mismatch' { $tier.Size = [uint64]101 }
    'write-cache-nonzero' { $virtual.WriteCacheSize = [uint64]1 }
    'layout-bool' { $tier.NumberOfColumns = $true }
    'multiple-tiers' { $virtualTiers += [pscustomobject]@{ UniqueId='second-tier-uid'; ObjectId='second-tier-object' } }
    'ambiguous-pool-members' { $script:poolMembers += [pscustomobject]@{ UniqueId='second-uid'; ObjectId='second-object'; MediaType='HDD' } }
    'virtual-layout-fields-null' {
        $virtual.ResiliencySettingName = $null
        $virtual.ProvisioningType = $null
        $virtual.NumberOfColumns = $null
        $virtual.Interleave = $null
        $virtual.NumberOfDataCopies = $null
        $virtual.PhysicalDiskRedundancy = $null
    }
    'virtual-layout-fields-incompatible' { $virtual.ProvisioningType = 'Dynamic' }
    'virtual-layout-field-missing' { [void]$virtual.PSObject.Properties.Remove('ProvisioningType') }
}
function Get-AssociationKey($Item, [string]$Fallback) {
    if (-not [string]::IsNullOrWhiteSpace([string]$Item.UniqueId)) { return "uid:$([string]$Item.UniqueId)" }
    if (-not [string]::IsNullOrWhiteSpace([string]$Item.ObjectId)) { return "oid:$([string]$Item.ObjectId)" }
    return "fallback:$Fallback"
}
function Get-StoragePool {
    [CmdletBinding()]
    param($VirtualDisk, $StorageTier)
    if ($null -ne $VirtualDisk) { return $script:poolParents }
    throw 'unexpected-pool-query'
}
function Get-PhysicalDisk {
    [CmdletBinding()]
    param($StoragePool, $VirtualDisk, [bool]$HasAllocations)
    if ($null -ne $VirtualDisk) {
        $script:allocationQueries++
        if (-not $PSBoundParameters.ContainsKey('HasAllocations') -or -not $HasAllocations) { throw 'missing-has-allocations-filter' }
        if ([string]$fixture.Scenario -eq 'allocation-query-throws') { throw 'allocation-query-failed' }
        return $script:allocatedMembers
    }
    if ($null -ne $StoragePool) { return $script:poolMembers }
    throw 'unexpected-physical-disk-query'
}
function Invoke-CimMethod {
    [CmdletBinding()]
    param($InputObject, [string]$MethodName)
    $script:extentCalls++
    if ($MethodName -ne 'GetPhysicalExtent' -or $InputObject.UniqueId -ne 'tier-uid') { throw 'unexpected-extent-query' }
    $extent = [pscustomobject]@{
        StorageTierUniqueId='tier-uid'; VirtualDiskUniqueId='virtual-uid'; PhysicalDiskUniqueId='physical-uid'; Size=[uint64]100
    }
    return [pscustomobject]@{ ReturnValue=[uint32]0; PhysicalExtents=@($extent) }
}
""" + EmbeddedStorageInventoryScript.TierMemberAssociationHelpers + """
$keys = @(Get-ExactTierMemberKeys $tier $virtual $pool $virtualTiers)
[ordered]@{ Keys=@($keys); ExtentCalls=$script:extentCalls; AllocationQueries=$script:allocationQueries;
    SourceQueryFailures=@($sourceQueryFailures.ToArray()) } | ConvertTo-Json -Depth 8 -Compress
""";

        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(WindowsPowerShellStorageWriteRunner.EncodeFixedCompressedScript(harness));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Scenario = scenario }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, await error);
        Assert.True(string.IsNullOrWhiteSpace(await error), await error);
        return JsonDocument.Parse(await output);
    }
}
