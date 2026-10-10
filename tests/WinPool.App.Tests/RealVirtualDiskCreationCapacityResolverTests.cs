using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealVirtualDiskCreationCapacityResolverTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public async Task NativeMaximumSkipsProviderRangeAndCarriesNoEstimatedBytes()
    {
        var rangeReads = 0;
        var capacity = await RealVirtualDiskCreationCapacityResolver.ResolveAsync(
            useMaximumSize: true,
            requestedSizeBytes: 32L * 1024 * MiB,
            autoCreatePartition: true,
            createMsr: true,
            readRangeAsync: () =>
            {
                rangeReads++;
                throw new InvalidOperationException("MAX must not query or infer a provider capacity.");
            });

        Assert.Equal(new RealVirtualDiskCreationCapacity(0, true), capacity);
        Assert.Equal(0, rangeReads);
    }

    [Fact]
    public async Task ExplicitCapacityReadsFreshRangeAndPreservesExactBytes()
    {
        var target = new StorageObjectId(SystemId.New(), StorageObjectKind.StoragePool, "pool");
        var range = new RealVirtualDiskCreationRange(target,
            8 * MiB, 32 * MiB, 4 * MiB, 8 * MiB, [],
            "fresh-pool-fingerprint", DateTimeOffset.UtcNow);
        var rangeReads = 0;

        var capacity = await RealVirtualDiskCreationCapacityResolver.ResolveAsync(
            useMaximumSize: false,
            requestedSizeBytes: 16 * MiB,
            autoCreatePartition: true,
            createMsr: false,
            readRangeAsync: () =>
            {
                rangeReads++;
                return Task.FromResult(range);
            });

        Assert.Equal(new RealVirtualDiskCreationCapacity(16 * MiB, false), capacity);
        Assert.Equal(1, rangeReads);
    }

    [Fact]
    public async Task ExplicitCapacityOutsideFreshProviderGridIsRejected()
    {
        var target = new StorageObjectId(SystemId.New(), StorageObjectKind.StorageTier, "hdd-template");
        var range = new RealVirtualDiskCreationRange(target,
            8 * MiB, 32 * MiB, 4 * MiB, 8 * MiB, [],
            "fresh-template-fingerprint", DateTimeOffset.UtcNow);
        var rangeReads = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => RealVirtualDiskCreationCapacityResolver.ResolveAsync(
            useMaximumSize: false,
            requestedSizeBytes: 17 * MiB,
            autoCreatePartition: false,
            createMsr: false,
            readRangeAsync: () =>
            {
                rangeReads++;
                return Task.FromResult(range);
            }));

        Assert.Equal(1, rangeReads);
    }

    [Fact]
    public void FrozenOrdinaryMaximumConfirmationNamesWindowsAndDoesNotDisplayZeroBytes()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(system, pool,
            new RealOperationProposalFactory.VirtualDiskOptions(
                "MaximumVD", 0, false, false, false, null, null, UseMaximumSize: true));
        var plan = Freeze(proposal);

        var chinese = RealOperationConfirmationFormatter.Format(plan, chinese: true);
        var english = RealOperationConfirmationFormatter.Format(plan, chinese: false);

        Assert.Contains("最大可用容量（执行时测定）", chinese);
        Assert.DoesNotContain("UseMaximumSize=true", chinese);
        Assert.DoesNotContain("sizeBytes=0", chinese);
        Assert.Contains("maximum available capacity (measured during the operation)", english);
        Assert.DoesNotContain("UseMaximumSize=true", english);
        Assert.DoesNotContain("sizeBytes=0", english);
    }

    [Fact]
    public void TieredMaximumCarriesSearchIntentForAgentFreezing()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var tier = new StorageObjectId(system, StorageObjectKind.StorageTier, "hdd-template-id");

        var proposal = RealOperationProposalFactory.CreateTieredVirtualDisk(
            system, pool, tier, "MaximumVD", 0, useMaximumSize: true);
        var command = Assert.IsType<CreateTieredVirtualDiskCommand>(Assert.Single(proposal.Steps).Command);
        Assert.True(command.UseMaximumSize);
        Assert.Null(command.MaximumCapacity); // Only the Agent may freeze live search inputs.
        Assert.Equal(TieredVirtualDiskCreationMechanism.ExactTemplate, command.CreationMechanism);
    }

    [Fact]
    public void FrozenMaximumSearchShowsUserRelevantCandidateWithoutInternalSearchDiagnostics()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(system, pool,
            new RealOperationProposalFactory.VirtualDiskOptions("MaximumVD", 0, false, false, false, null, null, true));
        var upper = 100 * MaximumCapacityAlgorithm.GiB;
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, upper,
            MaximumCapacityAlgorithm.InitialCandidateBytes(upper), upper, 204,
            "provider range", "source-fingerprint", DateTimeOffset.UtcNow);
        proposal = proposal with { Steps = proposal.Steps.Select(step => step with
        {
            Command = ((CreateVirtualDiskCommand)step.Command) with { MaximumCapacity = policy }
        }).ToArray() };
        var text = RealOperationConfirmationFormatter.Format(Freeze(proposal), chinese: false);
        Assert.Contains("starting candidate", text);
        Assert.Contains("99 GiB", text);
        Assert.Contains("maximum-capacity search", text);
        Assert.DoesNotContain("4,000,000 bytes", text);
        Assert.DoesNotContain("descend 1 GiB", text);
        Assert.DoesNotContain("ascend 1 GiB", text);
        Assert.DoesNotContain("source-fingerprint", text);
        Assert.DoesNotContain("fingerprint", text);
        Assert.DoesNotContain("actual capacity determined by Windows", text);
    }

    [Fact]
    public void ExplicitTieredCreationConfirmationNamesTheExactTemplateMechanism()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var tier = new StorageObjectId(system, StorageObjectKind.StorageTier, "hdd-template-id");
        var proposal = RealOperationProposalFactory.CreateTieredVirtualDisk(
            system, pool, tier, "ExplicitVD", 16 * MiB);
        var command = Assert.IsType<CreateTieredVirtualDiskCommand>(Assert.Single(proposal.Steps).Command);
        Assert.Equal(TieredVirtualDiskCreationMechanism.ExactTemplate, command.CreationMechanism);

        var text = RealOperationConfirmationFormatter.Format(Freeze(proposal), chinese: false);

        Assert.Contains("storage pool (pool-id)", text);
        Assert.Contains("storage tier (hdd-template-id)", text);
        Assert.Contains("16,777,216 bytes", text);
        Assert.DoesNotContain("WindowsAutomaticHdd", text);
    }

    [Fact]
    public void ExplicitCapacityConfirmationKeepsExactBytes()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        const long bytes = 16L * 1024 * 1024 * 1024;
        var proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(system, pool,
            new RealOperationProposalFactory.VirtualDiskOptions(
                "ExplicitVD", bytes, false, false, false, null, null));

        var text = RealOperationConfirmationFormatter.Format(Freeze(proposal), chinese: false);

        Assert.Contains("17,179,869,184 bytes", text);
        Assert.DoesNotContain("UseMaximumSize=true", text);
        Assert.DoesNotContain("actual capacity determined by Windows", text);
    }

    private static OperationPlan Freeze(RealOperationIntentRequest proposal)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product-session",
            Guid.NewGuid().ToString("D"), 42, now,
            Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(),
            EnvironmentKind.LocalMachine, "machine-binding",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
            false, now);
        return RealOperationPlanFactory.Create(proposal,
            OperationId.New(), environment, session, "inventory",
            "target-fingerprint", "physical-fingerprint", "support", now,
            now.AddMinutes(2));
    }
}
