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

        var repositoryRoot = FindRepositoryRoot();
        var document = await new WindowsRealStorageFactSource()
            .CaptureFreshAsync(CancellationToken.None);
        var facts = document.SourceFacts
            ?? throw new InvalidDataException("The fresh storage collector returned no source facts.");
        var snapshot = document.Snapshot;
        var machineBinding = await new WindowsRealMachineIdentityProvider()
            .ReadBindingAsync(CancellationToken.None);
        var selectedPhysicalId = Environment.GetEnvironmentVariable(
            "WINPOOL_ADMISSION_PHYSICAL_ID");
        string topologyState;
        object[] targetProbes = [];
        object? selectedSafetyProbe = null;
        object? selectedPlanProbe = null;
        string? selectedPlanStatus = null;
        object? c01FormatCapability = null;
        WindowsTierCapability? c04TierCapability = null;
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
                        closure.PoolMemberRoleEvidence,
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

            if (!string.IsNullOrWhiteSpace(selectedPhysicalId))
            {
                var physical = snapshot.PhysicalDisks.Single(item =>
                    StringComparer.Ordinal.Equals(item.StableId, selectedPhysicalId));
                var physicalId = new StorageObjectId(document.SystemId,
                    StorageObjectKind.PhysicalDisk, physical.StableId);
                var capabilityClosure = topology.RequireSinglePhysicalClosure([physicalId]);
                var capabilityReader = new WindowsRealStorageCapabilityReader();
                var volumeCapabilities = new List<WindowsVolumeFormatCapability>();
                foreach (var volume in capabilityClosure.Objects.Where(item =>
                             item.ObjectType == FactObjectType.Volume))
                {
                    volumeCapabilities.Add(await capabilityReader.ReadVolumeFormatAsync(
                        topology, new StorageObjectId(document.SystemId,
                            StorageObjectKind.Volume, volume.Id), CancellationToken.None));
                }
                c01FormatCapability = new
                {
                    scope = "current_exact_volumes_only",
                    status = volumeCapabilities.Count == 0 ? "unknown_no_current_volume" : "queried",
                    physical.StableId,
                    capabilityClosure.PhysicalMemberFingerprint,
                    note = "Read-only preliminary provider evidence; the H05 1024 MiB candidate must be queried again. No format capability is enabled by this report.",
                    volumes = volumeCapabilities.ToArray()
                };
                c04TierCapability = await capabilityReader.ReadTierAsync(
                    topology, physicalId, CancellationToken.None);
                var osDisk = snapshot.OsDisks.Single(item =>
                    item.PhysicalDiskStableId == physical.StableId);
                var osDiskId = new StorageObjectId(document.SystemId,
                    StorageObjectKind.OsDisk, osDisk.StableId);
                var clearCommand = new ClearDiskCommand(
                    RealTargetReference.ForExisting(osDiskId), false);
                var closure = topology.RequireSinglePhysicalClosure(
                    [osDiskId]);
                var exact = WindowsRealStorageTargetBuilder.Build(topology,
                    RealTargetReference.ForExisting(osDiskId),
                    new Dictionary<string, string>());
                string safety;
                try
                {
                    await new WindowsRealStorageSafetyInspector().ValidateAsync(
                        topology, closure, clearCommand,
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

                // Probe the Windows planner with the raw provider's SystemId.
                // This does not cover the App/Agent canonical identity binding,
                // persisted preparation, confirmation, or acceptance.
                var proposal = new RealOperationIntentRequest(
                    OperationIntent.ClearDisk, document.SystemId, [osDiskId],
                    [new RealOperationStep("operation-1", clearCommand, [],
                        "Agent live target verification required",
                        RealOperationValidator.ClearDiskExpectedFinalState,
                        "All partitions, volumes, files and drive letters on the exact disk are lost",
                        "Agent live Windows preflight required")],
                    RealOperationValidator.ClearDiskExpectedFinalState);
                var session = new TrustedRealSession(SessionId.New(),
                    "read-only-admission", "read-only-admission", Environment.ProcessId,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory,
                        "testhost.exe"), true);
                var dataRoot = StorageDataLocations.ResolveCurrentRoot(
                    Path.Combine(repositoryRoot, "artifacts", "Release"));
                var planner = new WindowsRealOperationPlanner(
                    privilege: new WindowsPrivilegeService(),
                    safetyInspector: new WindowsRealStorageSafetyInspector([dataRoot]));
                try
                {
                    var plan = await planner.PrepareAsync(proposal, session,
                        OperationId.New(), CancellationToken.None);
                    selectedPlanStatus = "prepared";
                    selectedPlanProbe = new
                    {
                        scope = "raw_provider_identity_planner_only",
                        status = selectedPlanStatus,
                        plan.OperationId,
                        plan.PlanHash,
                        plan.Risk,
                        stepCount = plan.RealOperation?.Steps.Count
                    };
                }
                catch (Exception exception)
                {
                    selectedPlanStatus = exception.GetType().Name + ": "
                        + exception.Message;
                    selectedPlanProbe = new
                    {
                        scope = "raw_provider_identity_planner_only",
                        status = "rejected",
                        exceptionType = exception.GetType().FullName,
                        exception.Message,
                        exception.StackTrace
                    };
                }
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
            selectedSafetyProbe,
            selectedPlanProbe,
            c01FormatCapability,
            c04TierCapability
        };
        var directory = Path.Combine(repositoryRoot,
            "artifacts", "test-results", "real-admission-p0");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            $"read-only-topology-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        await using (var evidence = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(evidence, report,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
        }
        output.WriteLine("Read-only admission evidence saved locally: " + path);
        output.WriteLine("Topology status: " + topologyState);
        Assert.Equal(StorageSystemKind.Local, document.Kind);
        Assert.NotEmpty(snapshot.PhysicalDisks);
        if (!string.IsNullOrWhiteSpace(selectedPhysicalId))
            Assert.True(selectedPlanStatus == "prepared",
                "Selected read-only plan probe failed. Topology: " + topologyState
                + "; planner: " + (selectedPlanStatus ?? "not reached"));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinPool.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException(
            "WinPool.slnx was not found above the test binary directory: "
            + AppContext.BaseDirectory);
    }
}
