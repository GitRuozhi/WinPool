using WinPool.Application;
using WinPool.Core;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class LegacySimulationEditCoordinatorTests
{
    [Fact]
    public async Task EditPassesThroughPlanAuthorizationAndSimulationExecutorBeforeCommit()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        StorageSystemDocument? committed = null;
        LegacySimulationEditCommit? structuredCommit = null;
        var coordinator = new LegacySimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                structuredCommit = commit;
                committed = commit.Document;
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.Rename,
                "physical:p1",
                Name: "Renamed disk"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(committed);
        Assert.NotNull(structuredCommit);
        Assert.Equal(result.Value!.PlanHash, structuredCommit.Plan.PlanHash);
        Assert.Equal(
            WinPool.Execution.ExecutionEventKind.Completed,
            structuredCommit.Events[^1].Kind);
        Assert.Equal(
            "Renamed disk",
            committed.Snapshot.PhysicalDisks.Single().FriendlyName);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PlanHash));
        Assert.Equal("physical:p1", result.Value.Target.ProviderKey);
        Assert.Equal(result.Value.BeforeRevision + 1, result.Value.AfterRevision);
        Assert.Contains("Set-StorageObject", Assert.Single(result.Value.SimulatedCommands));
    }

    [Fact]
    public async Task LocalDocumentIsRejectedBeforeLegacyEditorOrCommit()
    {
        var committed = false;
        var coordinator = new LegacySimulationEditCoordinator(
            () => CreateDocument(StorageSystemKind.Local),
            (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            new ThrowingLegacyEditor());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.Rename,
                "physical:p1",
                Name: "Forbidden"),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.local-read-only", Assert.Single(result.Messages).Code);
        Assert.False(committed);
    }

    [Fact]
    public async Task InvalidLegacyEditDoesNotCommitPartialDocument()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var committed = false;
        var coordinator = new LegacySimulationEditCoordinator(
            () => active,
            (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
                "physical:p1",
                FileSystem: "NTFS"),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Failed, result.Status);
        Assert.False(committed);
        Assert.Equal("simulation.failed", Assert.Single(result.Messages).Code);
    }

    [Fact]
    public async Task PrimordialUiAliasResolvesToStablePoolAndBindsMemberDisk()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var coordinator = new LegacySimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.CreateStoragePool,
                "primordial",
                Name: "Pool03",
                MemberDiskIds: ["physical:p1"]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("pool:primordial", result.Value!.Target.ProviderKey);
        Assert.Contains(active.Snapshot.StoragePools, pool => pool.FriendlyName == "Pool03");
        Assert.DoesNotContain(
            "physical:p1",
            active.Snapshot.StoragePools.Single(pool => pool.IsPrimordial).MemberPhysicalDiskIds);
    }

    [Fact]
    public async Task BuiltInResetAlsoUsesTheAuthorizedSimulationExecutionChain()
    {
        var active = CreateDocument(StorageSystemKind.Simulation) with
        {
            Id = "simulation:builtin:test"
        };
        var coordinator = new LegacySimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService(),
            document => new SimulationOperationResult(
                true,
                document with { DisplayName = "Reset document" },
                string.Empty,
                ["Reset-SimulationDocument -BuiltIn"]));

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.ResetDocument,
                active.Snapshot.Computer.StableId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Reset document", active.DisplayName);
        Assert.Equal(
            "Reset-SimulationDocument -BuiltIn",
            Assert.Single(result.Value!.SimulatedCommands));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PlanHash));
    }

    [Fact]
    public async Task ImportedSimulationCannotUseBuiltInResetContract()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var resetCalled = false;
        var coordinator = new LegacySimulationEditCoordinator(
            () => active,
            (_, _) => Task.CompletedTask,
            new SimulationOperationService(),
            document =>
            {
                resetCalled = true;
                return SimulationOperationResult.Failure(document, "Unexpected reset.");
            });

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.ResetDocument,
                active.Snapshot.Computer.StableId),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.reset-built-in-only", Assert.Single(result.Messages).Code);
        Assert.False(resetCalled);
    }

    private static StorageSystemDocument CreateDocument(StorageSystemKind kind)
    {
        var snapshot = new StorageSnapshot(
            2,
            "test",
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            new ComputerInfo(
                "system:test",
                "TEST-PC",
                "Windows",
                "10.0",
                "19045",
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [
                new PhysicalDiskInfo(
                    "physical:p1", true, "Disk One", "Model", "masked", "SATA", "SSD",
                    1_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 5,
                    false, false, false, false, "pool:primordial")
            ],
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    1_000_000_000, 0, "subsystem:1", ["physical:p1"])
            ],
            [],
            [],
            [
                new OsDiskInfo(
                    "osdisk:5", "Disk One", 5, "RAW", 1_000_000_000,
                    false, false, false, "physical:p1", null)
            ],
            [],
            [],
            [],
            []);
        return new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            kind == StorageSystemKind.Local ? "local:test" : "simulation:test",
            kind,
            "Test",
            snapshot,
            HardwareInventoryReport.Empty(DateTimeOffset.MinValue),
            [],
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_001));
    }

    private sealed class ThrowingLegacyEditor : ISimulationOperationService
    {
        public SimulationOperationResult Apply(
            StorageSystemDocument document,
            SimulationOperationRequest request) =>
            throw new InvalidOperationException("The legacy editor must not be invoked.");
    }
}
