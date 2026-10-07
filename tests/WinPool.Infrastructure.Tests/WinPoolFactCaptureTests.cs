using System.Text.Json;
using System.Diagnostics;
using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WinPoolFactCaptureTests
{
    [Theory]
    [InlineData("DeviceNumberFallback", true, true, "16", "", "uid:virtual", 0)]
    [InlineData("DeviceNumberFallback", true, true, "11", "", "uid:virtual", 0)]
    [InlineData("ProviderUniqueId", true, true, "16", "", "uid:virtual", 1)]
    [InlineData("ProviderUniqueId", true, false, "11", "uid:physical", "", 0)]
    [InlineData("DeviceNumberFallback", true, false, "11", "uid:physical", "", 0)]
    [InlineData("DeviceNumberFallback", true, false, "16", "", "", 2)]
    [InlineData("ProviderUniqueId", true, false, "Spaces", "", "", 2)]
    [InlineData("", false, false, "11", "", "", 1)]
    [InlineData("", false, true, "16", "", "uid:virtual", 0)]
    public async Task FixedOsDiskParentBindingHandlesCollisionUnmappedDiskAndConflictingProviderIdentity(
        string origin, bool physical, bool virtualDisk, string busType, string expectedPhysical, string expectedVirtual, int failures)
    {
        // Execute only the same fixed parent function embedded in the collector.
        // Inputs are synthetic maps; the harness invokes no storage cmdlets.
        var harness = EmbeddedStorageInventoryScript.OsDiskParentBinding + """
        $data = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $sourceQueryFailures = [System.Collections.Generic.List[object]]::new()
        $diskPhysicalMap = @{}
        $diskPhysicalMapSource = @{}
        $virtualDiskKeyByOsDisk = @{}
        if ($data.Physical) { $diskPhysicalMap[0]='uid:physical'; $diskPhysicalMapSource[0]=$data.Origin }
        if ($data.VirtualDisk) { $virtualDiskKeyByOsDisk[0]='uid:virtual' }
        $parents = Resolve-OsDiskDeviceParents ([pscustomobject]@{ Number=0; BusType=$data.BusType })
        [ordered]@{ Physical=$parents.PhysicalDiskAssociationKey; Virtual=$parents.VirtualDiskAssociationKey;
            Failures=@($sourceQueryFailures.ToArray()) } | ConvertTo-Json -Depth 5 -Compress
        """;
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + harness)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Origin = origin, Physical = physical, VirtualDisk = virtualDisk, BusType = busType }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        using var result = JsonDocument.Parse(await output);
        Assert.Equal(expectedPhysical, result.RootElement.GetProperty("Physical").GetString());
        Assert.Equal(expectedVirtual, result.RootElement.GetProperty("Virtual").GetString());
        var errors = result.RootElement.GetProperty("Failures").EnumerateArray().ToArray();
        Assert.Equal(failures, errors.Length);
        Assert.All(errors, item => Assert.Equal("MSFT_Disk", item.GetProperty("ClassName").GetString()));
        if (physical && virtualDisk && origin == "ProviderUniqueId")
            Assert.Contains(errors, item => item.GetProperty("ReasonCode").GetString() == "ConflictingPhysicalAndVirtualDiskParents");
    }

    [Fact]
    public void CollectorBuildsVirtualOwnershipBeforeAllowingPhysicalNumberFallback()
    {
        var script = EmbeddedStorageInventoryScript.ForPurpose(CollectionPurpose.Storage);
        var vdMap = script.IndexOf("$virtualDiskKeyByOsDisk[[int]$mappedDisk.Number] = $virtualKey", StringComparison.Ordinal);
        var physicalMap = script.IndexOf("$diskPhysicalMap = @{}", StringComparison.Ordinal);
        Assert.True(vdMap >= 0 && physicalMap > vdMap);
        Assert.Contains("-not $virtualDiskKeyByOsDisk.ContainsKey([int]$disk.Number)", script);
        Assert.Contains("PhysicalDiskAssociationKey = $deviceParents.PhysicalDiskAssociationKey", script);
        Assert.Contains("VirtualDiskAssociationKey = $deviceParents.VirtualDiskAssociationKey", script);
        Assert.DoesNotContain("$disk | Get-PhysicalDisk | Select-Object -First 1", script);
        Assert.Contains("VirtualDiskToCurrentOsDiskAssociationNotUnique", script);
    }

    [Fact]
    public void FixedCollectorRecordsPoolMemberAndVirtualDiskAssociationFailures()
    {
        var script = EmbeddedStorageInventoryScript.ForPurpose(CollectionPurpose.Storage);
        Assert.Contains("$members = @(Get-SourceSet 'MSFT_PhysicalDisk' { Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop })", script);
        Assert.Contains("Get-SourceSet 'MSFT_Disk' { Get-Disk -VirtualDisk $virtual -ErrorAction Stop }", script);
        Assert.DoesNotContain("foreach ($mappedDisk in @(Get-Disk -VirtualDisk $virtual))", script);
    }

    [Fact]
    public void UnresolvedPoolMemberCannotDisappearFromAnOtherwiseSuccessfulCapture()
    {
        using var json = JsonDocument.Parse("""
            {"SourceObservations":[{"ClassName":"MSFT_StoragePool","Namespace":"root/Microsoft/Windows/Storage","Identity":"pool",
                "Fields":[{"Name":"UniqueId","CimType":"String","Value":"pool","ReadState":"Returned"}]}],
             "StoragePools":[{"UniqueId":"pool","MemberPhysicalDiskKeys":["uid:missing-physical"]}]}
            """);
        var facts = WinPoolFactCapture.Read(json.RootElement, StorageSnapshot.Empty("test"), SystemId.New(), CollectionPurpose.Storage);
        Assert.Contains(facts.Sources, source => source.ClassName == "MSFT_PhysicalDisk"
            && source.ReadState == FieldReadState.Failed && source.ReasonCode == "PoolMemberAssociationNotExact");
        Assert.Empty(facts.Relationships);
        Assert.Equal(FieldReadState.Failed, Assert.Single(facts.Collections).State);
    }

    [Fact]
    public void StorageCollectionDiscoversExactLocalProviderBeforeReadingPhysicalDisks()
    {
        var script = EmbeddedStorageInventoryScript.ForPurpose(CollectionPurpose.Storage);
        ReadOnlyStorageCommandPolicy.EnsureSafe(script);
        const string refresh = "Update-StorageProviderCache -StorageSubSystem $subsystem -DiscoveryLevel Level3 -ErrorAction Stop";
        var scope = script.IndexOf("$physicalObjects = @(Get-SourceSet 'MSFT_PhysicalDisk' {", StringComparison.Ordinal);
        var discovery = script.IndexOf(refresh, StringComparison.Ordinal);
        var query = script.IndexOf("Get-PhysicalDisk -ErrorAction Stop", discovery, StringComparison.Ordinal);
        Assert.True(scope >= 0 && discovery > scope && query > discovery);
        Assert.Contains("$subsystemObjects.Count -ne 1", script);
        Assert.Contains("$subsystem.CimClass.CimClassName", script);
        Assert.Contains("$subsystem.CimSystemProperties.Namespace", script);
        Assert.Contains("$subsystem.CimSystemProperties.ServerName, [Environment]::MachineName", script);
        Assert.Contains("[string]::IsNullOrWhiteSpace([string]$subsystem.UniqueId)", script);
        Assert.Contains("[string]::IsNullOrWhiteSpace([string]$subsystem.ObjectId)", script);
        Assert.Contains("StorageProviderCacheDiscoveryFailed:", script);
        Assert.Contains("ClassName=$ClassName; Namespace=$Namespace; ReasonCode=[string]$_.FullyQualifiedErrorId", script);
        Assert.DoesNotContain("-FriendlyName", script.Substring(scope, query - scope));
        Assert.Equal(60, WindowsPowerShellRunner.TimeoutSeconds);
    }

    [Fact]
    public void HardwareCollectionKeepsDiscoveryRefreshOutOfItsFixedCommand()
    {
        var script = EmbeddedStorageInventoryScript.ForPurpose(CollectionPurpose.Hardware);
        ReadOnlyStorageCommandPolicy.EnsureSafe(script);
        Assert.DoesNotContain("Update-StorageProviderCache", script);
        Assert.Contains("$physicalObjects = @(Get-SourceSet 'MSFT_PhysicalDisk' { Get-PhysicalDisk -ErrorAction Stop })", script);
    }

    [Fact]
    public void ProviderDiscoveryFailureCannotBecomeASuccessfulPhysicalDiskSource()
    {
        using var json = JsonDocument.Parse("""
            {"SourceQuerySuccesses":[{"ClassName":"MSFT_PhysicalDisk","Namespace":"root/Microsoft/Windows/Storage"}],
             "SourceObservations":[{"ClassName":"MSFT_PhysicalDisk","Namespace":"root/Microsoft/Windows/Storage","Identity":"disk",
                "Fields":[{"Name":"UniqueId","CimType":"String","Value":"disk","ReadState":"Returned"}]}],
             "SourceQueryFailures":[{"ClassName":"MSFT_PhysicalDisk","Namespace":"root/Microsoft/Windows/Storage",
                "ReasonCode":"StorageProviderCacheDiscoveryFailed:subsystem-not-unique"}]}
            """);
        var facts = WinPoolFactCapture.Read(json.RootElement, StorageSnapshot.Empty("test"), SystemId.New(), CollectionPurpose.Storage);
        var source = Assert.Single(facts.Sources);
        Assert.Equal(FieldReadState.Failed, source.ReadState);
        Assert.Equal("StorageProviderCacheDiscoveryFailed:subsystem-not-unique", source.ReasonCode);
        Assert.Equal(FieldReadState.Failed, Assert.Single(facts.Collections).State);
        Assert.Equal(source.Id, Assert.Single(facts.Objects).SourceRef);
    }

    [Fact]
    public void TemplateEligibilityAndInstanceAllocationsKeepSeparateExactRelationships()
    {
        object Observation(string type, string id) => new
        {
            ClassName = type, Namespace = "root/Microsoft/Windows/Storage", Identity = id,
            Fields = new[] { new { Name = "UniqueId", Value = id, CimType = "String", ReadState = "Returned" } }
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            SourceObservations = new[]
            {
                Observation("MSFT_PhysicalDisk", "physical"), Observation("MSFT_StoragePool", "pool"),
                Observation("MSFT_StorageTier", "template"), Observation("MSFT_StorageTier", "instance"),
                Observation("MSFT_VirtualDisk", "vd")
            },
            StoragePools = new[] { new { UniqueId = "pool", PoolAssociationKey = "uid:pool", MemberPhysicalDiskKeys = new[] { "uid:physical" } } },
            VirtualDisks = new[] { new { UniqueId = "vd", PoolAssociationKey = "uid:pool" } },
            StorageTiers = new[]
            {
                new { UniqueId = "template", PoolAssociationKey = "uid:pool", VirtualDiskAssociationKey = "",
                    MemberPhysicalDiskKeys = Array.Empty<string>(), TemplatePhysicalDiskKeys = new[] { "uid:physical" } },
                new { UniqueId = "instance", PoolAssociationKey = "uid:pool", VirtualDiskAssociationKey = "uid:vd",
                    MemberPhysicalDiskKeys = new[] { "uid:physical" }, TemplatePhysicalDiskKeys = Array.Empty<string>() }
            }
        }));
        var facts = WinPoolFactCapture.Read(json.RootElement, StorageSnapshot.Empty("test"), SystemId.New(), CollectionPurpose.Storage);
        string Id(string unique) => facts.Objects.Single(item => item.Field("UniqueId")!.DisplayValue() == unique).Id;
        Assert.Contains(facts.Relationships, item => item.Kind == "template-pool-member" && item.FromId == Id("template") && item.ToId == Id("physical"));
        Assert.DoesNotContain(facts.Relationships, item => item.Kind == "tier-member" && item.FromId == Id("template"));
        Assert.Contains(facts.Relationships, item => item.Kind == "tier-member" && item.FromId == Id("instance") && item.ToId == Id("physical"));
        Assert.Contains(facts.Relationships, item => item.Kind == "virtual-disk-tier" && item.FromId == Id("vd") && item.ToId == Id("instance"));
        Assert.DoesNotContain(facts.Relationships, item => item.Kind == "virtual-disk-tier" && item.ToId == Id("template"));
    }

    [Fact]
    public void GraphicsWmiClassesAreSupplementsInsteadOfSecondDeviceLists()
    {
        object Field(string name, object value) => new { Name = name, Value = value, CimType = "String", ReadState = "Returned" };
        object Observation(string className, string identity) => new
        {
            ClassName = className,
            Namespace = className == "WmiMonitorID" ? "root/wmi" : "root/cimv2",
            Identity = identity,
            Fields = new[] { Field("Name", identity) }
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            SourceObservations = new[]
            {
                Observation("Win32_VideoController", "GPU0"),
                Observation("Win32_DesktopMonitor", "DISPLAY0"),
                Observation("WmiMonitorID", "MONITOR0")
            }
        }));

        var facts = WinPoolFactCapture.Read(json.RootElement, StorageSnapshot.Empty("test"), SystemId.New(), CollectionPurpose.Hardware);

        Assert.Equal(3, facts.Objects.Length);
        Assert.All(facts.Objects, item => Assert.Equal(FactObjectType.HardwareSupplement, item.ObjectType));
        Assert.Empty(facts.Objects.Where(item => item.ObjectType is FactObjectType.VideoController or FactObjectType.Monitor));
    }

    [Fact]
    public void LogicalDriveClassificationUsesDriveTypeAndKeepsLocalObservationWithItsVolume()
    {
        object Field(string name, object? value, string type = "String") => new { Name = name, Value = value, CimType = type, ReadState = "Returned" };
        object Logical(string letter, object? driveType) => new { ClassName = "Win32_LogicalDisk", Namespace = "root/cimv2", Identity = letter,
            Fields = new[] { Field("DeviceID", letter), Field("DriveType", driveType, "UInt32") } };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { SourceObservations = new object[] {
            new { ClassName = "MSFT_Volume", Namespace = "root/microsoft/windows/storage", Identity = "volume-id",
                Fields = new[] { Field("UniqueId", "volume-id"), Field("DriveLetter", "C") } },
            Logical("C:", 3), Logical("D:", 3), Logical("Z:", 4), Logical("Q:", null) } }));
        var facts = WinPoolFactCapture.Read(json.RootElement, StorageSnapshot.Empty("test"), SystemId.New(), CollectionPurpose.Hardware);
        var system = new WinPoolSystem(facts);
        Assert.Single(facts.Objects.Where(x => x.ObjectType == FactObjectType.NetworkDisk));
        var network = Assert.Single(system.Objects.Where(x => x.ObjectType == FactObjectType.Partition
            && x.Sources.Any(source => source.ObjectType == FactObjectType.NetworkDisk)));
        Assert.Single(network.Sources);
        var volume = Assert.Single(system.Objects.Where(x => x.ObjectType == FactObjectType.Partition
            && x.Sources.Any(source => source.ObjectType == FactObjectType.Volume)));
        Assert.Equal(2, volume.Sources.Length);
        Assert.Equal(5, facts.Objects.Length);
        Assert.Single(WinPoolStorageProjection.Project(facts).NetworkDisks);
    }

    [Fact]
    public void SuccessfulEmptyClassRemovesOldDevicesButFailedClassKeepsCachedFacts()
    {
        var system = SystemId.New();
        var time = DateTimeOffset.UtcNow;
        const string populated = """
            {"SourceObservations":[{"ClassName":"Win32_Processor","Namespace":"root\\cimv2","Identity":"DeviceID:CPU0",
            "Fields":[{"Name":"Name","CimType":"String","Value":"CPU","ReadState":"Returned"}]}]}
            """;
        WinPoolFacts Capture(string json, int seconds)
        {
            using var parsed = JsonDocument.Parse(json);
            return WinPoolFactCapture.Read(parsed.RootElement, StorageSnapshot.Empty("test") with { ScannedAt = time.AddSeconds(seconds) }, system, CollectionPurpose.Hardware);
        }
        var first = Capture(populated, -3);
        var second = Capture(populated, -2);
        Assert.Equal(first.Objects[0].Id, second.Objects[0].Id);
        var empty = Capture("""{"SourceQuerySuccesses":[{"ClassName":"Win32_Processor","Namespace":"root/cimv2"}]}""", -1);
        Assert.Empty(WinPoolFactRefresh.Merge(first, empty).Objects);
        var failed = Capture("""{"SourceQueryFailures":[{"ClassName":"Win32_Processor","Namespace":"root/cimv2"}]}""", 0);
        var retained = WinPoolFactRefresh.Merge(first, failed);
        Assert.Equal(first.Objects[0], Assert.Single(retained.Objects));
        Assert.Contains(retained.Sources, x => x.ReadState == FieldReadState.Failed);
    }

    [Fact]
    public async Task StorageRefreshOmitsHardwareAndFullRefreshReturnsTypedProcessorFacts()
    {
        var provider = new WindowsHardwareInventoryProvider();
        var storage = await provider.CollectLocalAsync(CancellationToken.None);
        Assert.NotNull(storage.SourceFacts);
        Assert.DoesNotContain(storage.SourceFacts.Objects, x => x.ObjectType == FactObjectType.Processor);
        Assert.Equal(CollectionPurpose.Storage, Assert.Single(storage.SourceFacts.Collections).Purpose);
        Assert.Contains(storage.SourceFacts.Objects, x => x.ObjectType == FactObjectType.PhysicalDisk);
        var projected = WinPoolStorageProjection.Project(storage.SourceFacts);
        Assert.Equal(storage.Snapshot.PhysicalDisks.Select(x => (x.StableId, x.Size, x.DeviceId)).OrderBy(x => x.StableId),
            projected.PhysicalDisks.Select(x => (x.StableId, x.Size, x.DeviceId)).OrderBy(x => x.StableId));
        Assert.Equal(storage.Snapshot.Partitions.Select(x => (x.StableId, x.Size, x.Offset, x.GptType)).OrderBy(x => x.StableId),
            projected.Partitions.Select(x => (x.StableId, x.Size, x.Offset, x.GptType)).OrderBy(x => x.StableId));
        Assert.All(projected.Volumes, volume => Assert.Contains(storage.SourceFacts.Objects, x => x.ObjectType == FactObjectType.Volume && x.Id == volume.StableId));
        var hardware = await provider.CollectHardwareAsync(CancellationToken.None);
        var cpu = Assert.Single(hardware.SourceFacts!.Objects.Where(x => x.ObjectType == FactObjectType.Processor));
        Assert.Equal(FieldReadState.Returned, cpu.Field("Name")!.ReadState);
        Assert.Equal(FactValueType.UInt64, cpu.Field("NumberOfCores")!.ValueType);
        var graphicsSource = Assert.Single(hardware.SourceFacts.Sources.Where(x => x.ClassName == "WinPool.GraphicsAdapter"));
        if (graphicsSource.ReadState == FieldReadState.Returned)
        {
            var graphics = hardware.SourceFacts.Objects.Where(x => x.SourceRef == graphicsSource.Id).ToArray();
            Assert.NotEmpty(graphics);
            Assert.All(graphics, item =>
            {
                Assert.Equal("bytes", item.Field("DedicatedVideoMemory")!.Unit);
                Assert.Equal("bytes", item.Field("SharedSystemMemory")!.Unit);
                Assert.Equal(FieldReadState.Returned, item.Field("DirectXFeatureLevel")!.ReadState);
                Assert.NotNull(item.Field("DxgiDescription"));
                Assert.NotNull(item.Field("AdapterTypeFlags"));
                Assert.NotNull(item.Field("IndirectDisplayDevice"));
                if (item.Field("IndirectDisplayDevice") is { ReadState: FieldReadState.Returned, Value: { } value }
                    && value.GetBoolean())
                {
                    Assert.Equal(FieldReadState.Returned, item.Field("DisplayDriverDescription")!.ReadState);
                    Assert.Equal(item.Field("DisplayDriverDescription")!.Value!.Value.GetString(), item.Field("Name")!.Value!.Value.GetString());
                }
            });
        }
        var networkSource = Assert.Single(hardware.SourceFacts.Sources.Where(x => x.ClassName == "WinPool.NetworkAdapter"));
        Assert.Equal(FieldReadState.Returned, networkSource.ReadState);
        var networkAdapters = hardware.SourceFacts.Objects.Where(x => x.SourceRef == networkSource.Id).ToArray();
        Assert.NotEmpty(networkAdapters);
        Assert.All(networkAdapters, adapter =>
        {
            Assert.Equal(FieldReadState.Returned, adapter.Field("InterfaceIndex")!.ReadState);
            Assert.True(adapter.Field("ConnectorPresent")!.Value!.Value.GetBoolean()
                || adapter.Field("InterfaceType")!.Value!.Value.GetUInt64() != 0);
        });
        var merged = WinPoolFactRefresh.Merge(storage.SourceFacts, hardware.SourceFacts);
        Assert.Equal(2, merged.Collections.Length);
        Assert.Equal(merged.Objects.Length, merged.Objects.Select(x => x.Id).Distinct().Count());
    }
}
