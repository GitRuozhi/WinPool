using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class ScopedStorageClosureTests
{
    [Fact]
    public async Task AttachedTierUsesItsExactVirtualDiskPoolWhileTemplateKeepsItsDirectPoolParent()
    {
        var json = await RunFixedClosureAsync("TierAttached");
        using var result = JsonDocument.Parse(json);
        Assert.False(result.RootElement.GetProperty("ClosureFailed").GetBoolean());
        var objects = result.RootElement.GetProperty("ClosureObjects").EnumerateArray().ToArray();
        Assert.Equal(new[] { "actual-tier-uid", "template-tier-uid" }, objects
            .Where(x => x.GetProperty("ClassName").GetString() == "MSFT_StorageTier")
            .Select(x => x.GetProperty("UniqueId").GetString()).Order(StringComparer.Ordinal));
        Assert.Single(objects, x => x.GetProperty("ClassName").GetString() == "MSFT_StoragePool");
        Assert.DoesNotContain(objects, x => x.GetProperty("UniqueId").GetString() is "samsung-sibling-uid" or "other-pool-uid");
        var document = await new WindowsHardwareInventoryProvider(new ResultRunner(json)).CollectScopedAsync(Scope(), default);
        Assert.True(document.SourceFacts!.ScopedCollection!.Complete);
        Assert.Equal(2, document.SourceFacts.Objects.Count(x => x.ObjectType == FactObjectType.StorageTier));
    }

    [Theory]
    [InlineData("TierMissingVirtualDisk")]
    [InlineData("TierAmbiguousVirtualDisk")]
    [InlineData("TierConflictingPool")]
    [InlineData("TierVirtualDiskMissingPool")]
    public async Task AttachedTierMissingAmbiguousOrConflictingParentNeverCompletes(string graph)
    {
        var json = await RunFixedClosureAsync(graph);
        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("ClosureFailed").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("SourceQueryFailures").EnumerateArray(), x =>
            x.GetProperty("ReasonCode").GetString()!.Contains("parent-unavailable", StringComparison.Ordinal));
        var document = await new WindowsHardwareInventoryProvider(new ResultRunner(json)).CollectScopedAsync(Scope(), default);
        Assert.False(document.SourceFacts!.ScopedCollection!.Complete);
        Assert.All(document.SourceFacts.Sources, x => Assert.False(x.Coverage!.Complete));
        Assert.Throws<InvalidDataException>(() => new WindowsRealStorageTopology(document, "mock-machine", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("PrimordialOnly")]
    [InlineData("DirectOnly")]
    [InlineData("ConcretePoolOnly")]
    public async Task PhysicalScopeRetainsExactSubsystemWithoutCollectingPrimordialSiblings(string graph)
    {
        var json = await RunFixedClosureAsync(graph);
        using var result = JsonDocument.Parse(json);
        Assert.False(result.RootElement.GetProperty("ClosureFailed").GetBoolean());
        var objects = result.RootElement.GetProperty("ClosureObjects").EnumerateArray().ToArray();
        Assert.Equal(graph == "ConcretePoolOnly"
                ? new[] { "MSFT_PhysicalDisk", "MSFT_StoragePool", "MSFT_StorageSubSystem" }
                : graph == "PrimordialOnly"
                    ? new[] { "MSFT_Disk", "MSFT_PhysicalDisk", "MSFT_StoragePool", "MSFT_StorageSubSystem" }
                    : new[] { "MSFT_Disk", "MSFT_PhysicalDisk", "MSFT_StorageSubSystem" },
            objects.Select(item => item.GetProperty("ClassName").GetString()).Order(StringComparer.Ordinal));
        var subsystem = Assert.Single(objects, item => item.GetProperty("ClassName").GetString() == "MSFT_StorageSubSystem");
        Assert.Equal("subsystem-uid", subsystem.GetProperty("UniqueId").GetString());
        Assert.Equal("subsystem-object", subsystem.GetProperty("ObjectId").GetString());
        Assert.DoesNotContain("primordial-object|MSFT_PhysicalDisk", result.RootElement.GetProperty("AssociationQueries")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain(objects, item => item.GetProperty("UniqueId").GetString() == "samsung-sibling-uid");

        var document = await new WindowsHardwareInventoryProvider(new ResultRunner(json))
            .CollectScopedAsync(Scope(), CancellationToken.None);
        Assert.True(document.SourceFacts!.ScopedCollection!.Complete);
        var fact = Assert.Single(document.SourceFacts.Objects, item => item.ObjectType == FactObjectType.StorageSubsystem);
        Assert.Equal("subsystem-uid", fact.Field("UniqueId")!.Value!.Value.GetString());
        Assert.Equal("subsystem-object", fact.Field("ObjectId")!.Value!.Value.GetString());
        if (graph is "ConcretePoolOnly" or "PrimordialOnly")
            Assert.Single(document.SourceFacts.Objects, item => item.ObjectType == FactObjectType.StoragePool);
        else Assert.DoesNotContain(document.SourceFacts.Objects, item => item.ObjectType == FactObjectType.StoragePool);
        Assert.Single(document.SourceFacts.Objects, item => item.ObjectType == FactObjectType.PhysicalDisk);
        if (graph == "PrimordialOnly")
        {
            var physical = Assert.Single(document.Snapshot.PhysicalDisks);
            var pool = Assert.Single(document.Snapshot.StoragePools);
            Assert.True(pool.IsPrimordial);
            Assert.Equal(new[] { physical.StableId }, pool.MemberPhysicalDiskIds);
            Assert.Equal(pool.StableId, physical.PoolStableId);
            Assert.False(Assert.Single(document.SourceFacts.Relationships, edge => edge.Kind == "pool-member").IsRetained);
            var node = Assert.Single(EditWorkspace.ProjectPoolWorkspace(document.Snapshot), item => item.Unit.StableId == pool.StableId);
            Assert.Equal(physical.StableId, Assert.Single(node.Children).Unit.StableId);
            Assert.Contains("Get-WinPoolScopePrimordialMembers $pool", ScopedStorageInventoryScript.Create(Scope()));
        }
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Ambiguous")]
    [InlineData("Conflicting")]
    [InlineData("ConcreteMissing")]
    [InlineData("ConcreteAmbiguous")]
    public async Task RawPhysicalScopeWithoutUniqueSubsystemIsIncompleteAndCannotSupplySafetyTopology(string graph)
    {
        var json = await RunFixedClosureAsync(graph);
        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("ClosureFailed").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("SourceQueryFailures").EnumerateArray(), item =>
            item.GetProperty("ReasonCode").GetString()!.Contains("subsystem-parent-unavailable", StringComparison.Ordinal));
        var document = await new WindowsHardwareInventoryProvider(new ResultRunner(json))
            .CollectScopedAsync(Scope(), CancellationToken.None);
        Assert.False(document.SourceFacts!.ScopedCollection!.Complete);
        Assert.All(document.SourceFacts.Sources, source => Assert.False(source.Coverage!.Complete));
        Assert.Throws<InvalidDataException>(() => new WindowsRealStorageTopology(document, "mock-machine", DateTimeOffset.UtcNow));
    }

    private static StorageInventoryScope Scope()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.PhysicalDisk,
            StableId.Create("physical", "physical-uid", "physical-object").Value);
        return new(system, OperationId.New(), "create-pool", [target], [target.ProviderKey],
            [new(target, "MSFT_PhysicalDisk", "UniqueId", "physical-uid")]);
    }

    private static async Task<string> RunFixedClosureAsync(string graph)
    {
        // Execute the production fixed closure with a closed synthetic CIM graph. Both CIM
        // entry points are shadowed; any undeclared query throws, and no storage cmdlet runs.
        var script = "$ProgressPreference='SilentlyContinue'; [Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8;\n"
            + MockGraph + "\n" + ScopedStorageInventoryScript.Closure + "\n" + CaptureResult;
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(WindowsPowerShellStorageWriteRunner.EncodeFixedCompressedScript(script));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Graph = graph }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        Assert.True(string.IsNullOrWhiteSpace(await error), await error);
        return await output;
    }

    private sealed class ResultRunner(string json) : IReadOnlyInventoryCommandRunner, IScopedInventoryCommandRunner
    {
        public Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No full or real provider query is allowed in this fixture.");
        public Task<ReadOnlyCommandResult> RunInventoryAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new ReadOnlyCommandResult(0, json, "", TimeSpan.FromMilliseconds(1)));
    }

    private const string MockGraph = """
$ErrorActionPreference = 'Stop'
$fixture = [Console]::In.ReadToEnd() | ConvertFrom-Json
$sourceQueryFailures = [System.Collections.Generic.List[object]]::new()
$AssociationQueries = [System.Collections.Generic.List[string]]::new()
function New-MockCim([string]$class, [string]$uid, [string]$oid, [bool]$primordial = $false) {
    return [pscustomobject]@{ UniqueId=$uid; ObjectId=$oid; IsPrimordial=$primordial;
        CimClass=[pscustomobject]@{ CimClassName=$class };
        CimSystemProperties=[pscustomobject]@{ Namespace='root/Microsoft/Windows/Storage'; ServerName=[Environment]::MachineName } }
}
$Physical = New-MockCim 'MSFT_PhysicalDisk' 'physical-uid' 'physical-object'
$Disk = New-MockCim 'MSFT_Disk' 'physical-uid' 'disk-object'
$Primordial = New-MockCim 'MSFT_StoragePool' 'primordial-uid' 'primordial-object' $true
$ConcretePool = New-MockCim 'MSFT_StoragePool' 'concrete-pool-uid' 'concrete-pool-object'
$Subsystem = New-MockCim 'MSFT_StorageSubSystem' 'subsystem-uid' 'subsystem-object'
$OtherSubsystem = New-MockCim 'MSFT_StorageSubSystem' 'other-subsystem-uid' 'other-subsystem-object'
$SamsungSibling = New-MockCim 'MSFT_PhysicalDisk' 'samsung-sibling-uid' 'samsung-sibling-object'
$VirtualDisk = New-MockCim 'MSFT_VirtualDisk' 'virtual-uid' 'virtual-object'
$OtherVirtualDisk = New-MockCim 'MSFT_VirtualDisk' 'other-virtual-uid' 'other-virtual-object'
$VirtualDiskOs = New-MockCim 'MSFT_Disk' 'virtual-uid' 'virtual-disk-object'
$TemplateTier = New-MockCim 'MSFT_StorageTier' 'template-tier-uid' 'template-tier-object'
$ActualTier = New-MockCim 'MSFT_StorageTier' 'actual-tier-uid' 'actual-tier-object'
$OtherPool = New-MockCim 'MSFT_StoragePool' 'other-pool-uid' 'other-pool-object'
$WinPoolScopeLocators = @([pscustomobject]@{ ClassName='MSFT_PhysicalDisk'; IdentityProperty='UniqueId'; IdentityValue='physical-uid' })
function Get-CimInstance {
    [CmdletBinding()] param($Namespace, $ClassName, $Filter)
    if ($Namespace -ne 'root/Microsoft/Windows/Storage' -or $Filter -ne "UniqueId = 'physical-uid'") { throw 'unexpected-mock-identity-query' }
    if ($ClassName -eq 'MSFT_PhysicalDisk') { return $Physical }
    if ($ClassName -eq 'MSFT_Disk') { if ($fixture.Graph -like 'Concrete*' -or $fixture.Graph -like 'Tier*') { return @() }; return $Disk }
    throw 'unexpected-mock-class-query'
}
function Get-CimAssociatedInstance {
    [CmdletBinding()] param($InputObject, $ResultClassName)
    [void]$AssociationQueries.Add(([string]$InputObject.ObjectId + '|' + [string]$ResultClassName))
    switch ([string]$InputObject.ObjectId) {
        'physical-object' {
            if ($ResultClassName -eq 'MSFT_StorageSubSystem') {
                if ($fixture.Graph -in @('DirectOnly','Conflicting')) { return $Subsystem }
                return @()
            }
            if ($ResultClassName -eq 'MSFT_StoragePool') {
                if ($fixture.Graph -like 'Concrete*' -or $fixture.Graph -like 'Tier*') { return $ConcretePool }
                if ($fixture.Graph -ne 'DirectOnly') { return $Primordial }
                return @()
            }
            if ($ResultClassName -eq 'MSFT_Disk') { return @() }
        }
        'primordial-object' {
            if ($ResultClassName -eq 'MSFT_StorageSubSystem') {
                if ($fixture.Graph -eq 'Missing') { return @() }
                if ($fixture.Graph -eq 'Ambiguous') { return @($Subsystem,$OtherSubsystem) }
                if ($fixture.Graph -eq 'Conflicting') { return $OtherSubsystem }
                return $Subsystem
            }
            # Expose a real sibling if code incorrectly traverses the availability pool.
            if ($ResultClassName -eq 'MSFT_PhysicalDisk') { return @($Physical,$SamsungSibling) }
        }
        'disk-object' {
            if ($ResultClassName -eq 'MSFT_PhysicalDisk') { return $Physical }
            if ($ResultClassName -in @('MSFT_VirtualDisk','MSFT_Partition')) { return @() }
        }
        'concrete-pool-object' {
            if ($ResultClassName -eq 'MSFT_PhysicalDisk') { return $Physical }
            if ($ResultClassName -eq 'MSFT_StorageSubSystem') {
                if ($fixture.Graph -eq 'ConcreteMissing') { return @() }
                if ($fixture.Graph -eq 'ConcreteAmbiguous') { return @($Subsystem,$OtherSubsystem) }
                return $Subsystem
            }
            if ($ResultClassName -eq 'MSFT_VirtualDisk') { if ($fixture.Graph -like 'Tier*') { return $VirtualDisk }; return @() }
            if ($ResultClassName -eq 'MSFT_StorageTier') { if ($fixture.Graph -like 'Tier*') { return $TemplateTier }; return @() }
        }
        'virtual-object' {
            if ($ResultClassName -eq 'MSFT_StoragePool') {
                if ($fixture.Graph -eq 'TierVirtualDiskMissingPool') { return @() }
                return $ConcretePool
            }
            if ($ResultClassName -eq 'MSFT_PhysicalDisk') { return $Physical }
            if ($ResultClassName -eq 'MSFT_StorageTier') { return $ActualTier }
            if ($ResultClassName -eq 'MSFT_Disk') { return $VirtualDiskOs }
        }
        'virtual-disk-object' {
            if ($ResultClassName -eq 'MSFT_VirtualDisk') { return $VirtualDisk }
            if ($ResultClassName -in @('MSFT_PhysicalDisk','MSFT_Partition')) { return @() }
        }
        'template-tier-object' {
            if ($ResultClassName -eq 'MSFT_StoragePool') { return $ConcretePool }
            if ($ResultClassName -eq 'MSFT_VirtualDisk') { return @() }
        }
        'actual-tier-object' {
            if ($ResultClassName -eq 'MSFT_StoragePool') {
                if ($fixture.Graph -eq 'TierConflictingPool') { return $OtherPool }
                return @()
            }
            if ($ResultClassName -eq 'MSFT_VirtualDisk') {
                if ($fixture.Graph -eq 'TierMissingVirtualDisk') { return @() }
                if ($fixture.Graph -eq 'TierAmbiguousVirtualDisk') { return @($VirtualDisk,$OtherVirtualDisk) }
                return $VirtualDisk
            }
        }
    }
    throw 'unexpected-mock-association-query'
}
""";

    private const string CaptureResult = """
$observations = @(foreach ($item in $WinPoolScopeObjects.Values) {
    $fields = @(foreach ($name in @('UniqueId','ObjectId')) {
        [ordered]@{ Name=$name; CimType='String'; ReadState='Returned'; Value=[string]$item.$name }
    })
    if ([string]$item.CimClass.CimClassName -eq 'MSFT_StoragePool') {
        $fields += [ordered]@{ Name='IsPrimordial'; CimType='Boolean'; ReadState='Returned'; Value=[bool]$item.IsPrimordial }
    }
    [ordered]@{ ClassName=[string]$item.CimClass.CimClassName; Namespace=$WinPoolScopeNamespace; Fields=$fields }
})
[ordered]@{
    ScannedAt=([DateTimeOffset]::Now).ToString('O'); Computer=[ordered]@{ Name=[Environment]::MachineName };
    StoragePools=@(foreach ($pool in @(Get-WinPoolScopeClass 'MSFT_StoragePool')) {
        $members = if ($pool.IsPrimordial) { @(Get-WinPoolScopePrimordialMembers $pool) } else { @($Physical) }
        [ordered]@{ UniqueId=[string]$pool.UniqueId; ObjectId=[string]$pool.ObjectId;
            SubsystemAssociationKey='uid:subsystem-uid';
            MemberPhysicalDiskKeys=@($members | ForEach-Object { 'uid:' + [string]$_.UniqueId }) }
    });
    SourceObservations=$observations;
    SourceQuerySuccesses=@($WinPoolScopeClasses | ForEach-Object { [ordered]@{ ClassName=$_; Namespace=$WinPoolScopeNamespace } });
    SourceQueryFailures=@($sourceQueryFailures.ToArray());
    ClosureFailed=$WinPoolScopeFailed;
    ClosureObjects=@($WinPoolScopeObjects.Values | ForEach-Object {
        [ordered]@{ ClassName=[string]$_.CimClass.CimClassName; UniqueId=[string]$_.UniqueId; ObjectId=[string]$_.ObjectId }
    });
    AssociationQueries=@($AssociationQueries.ToArray())
} | ConvertTo-Json -Depth 12 -Compress
""";
}
