using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.Execution;
using Xunit;
using Xunit.Abstractions;

namespace WinPool.Infrastructure.Tests;

public sealed class RealAdmissionReadOnlyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CaptureCurrentHostAdmissionFactsOnlyWhenExplicitlySelected()
    {
        if (Environment.GetEnvironmentVariable("WINPOOL_READ_ONLY_ADMISSION") != "1")
            return;

        var document = await new WindowsRealStorageFactSource()
            .CaptureFreshAsync(CancellationToken.None);
        var facts = document.SourceFacts
            ?? throw new InvalidDataException("The fresh storage collector returned no source facts.");
        var snapshot = document.Snapshot;
        var machineBinding = await new WindowsRealMachineIdentityProvider()
            .ReadBindingAsync(CancellationToken.None);
        string topologyState;
        object[] targetProbes = [];
        object? selectedSafetyProbe = null;
        try
        {
            var topology = new WindowsRealStorageTopology(
                document, machineBinding, DateTimeOffset.UtcNow);
            topologyState = "complete";
            targetProbes = snapshot.PhysicalDisks.Select(physical =>
            {
                try
                {
                    var closure = topology.RequireSinglePhysicalClosure(
                    [new StorageObjectId(document.SystemId,
                        StorageObjectKind.PhysicalDisk, physical.StableId)]);
                    return (object)new
                    {
                        physical.StableId,
                        status = "eligible_for_further_command_checks",
                        closure.Fingerprint,
                        closure.PhysicalMemberFingerprint,
                        relatedObjectIds = closure.Objects.Select(item => item.Id).ToArray()
                    };
                }
                catch (Exception exception)
                {
                    return new
                    {
                        physical.StableId,
                        status = "identity_or_topology_rejected",
                        reason = exception.Message
                    };
                }
            }).ToArray();

            var selectedPhysicalId = Environment.GetEnvironmentVariable(
                "WINPOOL_ADMISSION_PHYSICAL_ID");
            if (!string.IsNullOrWhiteSpace(selectedPhysicalId))
            {
                var physical = snapshot.PhysicalDisks.Single(item =>
                    StringComparer.Ordinal.Equals(item.StableId, selectedPhysicalId));
                var osDisk = snapshot.OsDisks.Single(item =>
                    item.PhysicalDiskStableId == physical.StableId);
                var closure = topology.RequireSinglePhysicalClosure(
                    [new StorageObjectId(document.SystemId,
                        StorageObjectKind.OsDisk, osDisk.StableId)]);
                var exact = WindowsRealStorageTargetBuilder.Build(topology,
                    RealTargetReference.ForExisting(new StorageObjectId(
                        document.SystemId, StorageObjectKind.OsDisk,
                        osDisk.StableId)), new Dictionary<string, string>());
                string safety;
                try
                {
                    await new WindowsRealStorageSafetyInspector().ValidateAsync(
                        topology, closure,
                        new ClearDiskCommand(RealTargetReference.ForExisting(
                            new StorageObjectId(document.SystemId,
                                StorageObjectKind.OsDisk, osDisk.StableId)), false),
                        CancellationToken.None);
                    safety = "read_only_safety_checks_passed";
                }
                catch (Exception exception)
                {
                    safety = exception.GetType().Name + ": " + exception.Message;
                }
                selectedSafetyProbe = new { physical.StableId, osDisk.Number,
                    exact, safety,
                    safetyFields = closure.Objects.Where(item =>
                        item.ObjectType is FactObjectType.Disk or FactObjectType.Partition)
                        .Select(item => new
                        {
                            item.Id,
                            item.ObjectType,
                            fields = item.Fields.Where(field => field.Name is
                                "IsReadOnly" or "IsClustered" or "IsOffline"
                                or "IsShadowCopy" or "PartitionStyle")
                                .Select(field => new { field.Name, field.ReadState,
                                    value = field.DisplayValue() }).ToArray()
                        }).ToArray() };
            }
        }
        catch (Exception exception)
        {
            topologyState = exception.GetType().Name + ": " + exception.Message;
        }

        var report = new
        {
            capturedAtUtc = facts.InventoryCapturedAt,
            machineBinding,
            topologyState,
            computer = snapshot.Computer,
            sourceStates = facts.Sources
                .Where(source => source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal))
                .Select(source => new
                {
                    source.ClassName,
                    source.Namespace,
                    source.ReadState,
                    source.ReasonCode
                }).ToArray(),
            physicalDisks = snapshot.PhysicalDisks,
            storagePools = snapshot.StoragePools,
            storageTiers = snapshot.StorageTiers,
            virtualDisks = snapshot.VirtualDisks,
            osDisks = snapshot.OsDisks,
            partitions = snapshot.Partitions,
            volumes = snapshot.Volumes,
            networkDisks = snapshot.NetworkDisks,
            relationships = snapshot.Relationships,
            fieldIssues = snapshot.FieldIssues,
            targetProbes,
            selectedSafetyProbe
        };
        var directory = Path.Combine(Directory.GetCurrentDirectory(),
            "artifacts", "test-results", "real-admission-p0");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "read-only-topology.json");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        output.WriteLine("Read-only admission evidence saved locally: " + path);
        output.WriteLine("Topology status: " + topologyState);
        Assert.Equal(StorageSystemKind.Local, document.Kind);
        Assert.NotEmpty(snapshot.PhysicalDisks);
    }
}
