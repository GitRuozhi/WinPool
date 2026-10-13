using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Execution.Tests;

public sealed class RealStorageLayoutContractTests
{
    private const long GiB = MaximumCapacityAlgorithm.GiB;
    private readonly SystemId _system = SystemId.New();
    private StorageObjectId Id(StorageObjectKind kind, string key) => new(_system, kind, key);
    private static RealOperationStep Step(string id, RealStorageCommand command, params string[] dependencies) =>
        new(id, command, dependencies, "Exact fresh identity", "Exact postcondition", "Declared impact", "Fresh provider evidence");
    private RealOperationIntentRequest Request(OperationIntent intent, IReadOnlyList<StorageObjectId> targets,
        params RealOperationStep[] steps) => new(intent, _system, targets, steps, "Exact requested state");

    [Theory]
    [InlineData("Simple", 2, 1, 0, 2, 16384)]
    [InlineData("Mirror", 2, 2, 1, 4, 32768)]
    [InlineData("Parity", 3, 1, 1, 3, 262144)]
    public void LayoutAndOrdinaryCreationAcceptLegalMultiMemberResiliency(
        string resiliency, int columns, int copies, int redundancy, int memberCount, int interleave)
    {
        var layout = new StorageLayoutParameters(resiliency, columns, interleave, copies, redundancy);
        Assert.True(layout.EstimateCapacity(Enumerable.Repeat(100 * GiB, memberCount).ToArray()).AlignedLogicalBytes > 0);
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var members = Enumerable.Range(0, memberCount).Select(index => Id(StorageObjectKind.PhysicalDisk, $"disk-{index}")).ToArray();
        RealOperationValidator.Validate(Request(OperationIntent.CreateVirtualDisk, new[] { pool }.Concat(members).ToArray(),
            Step("create", new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool), "VD", 10 * GiB,
                interleave, columns, Layout: layout))));
    }

    [Fact]
    public void LayoutUsesSharedMemberLegalityAndRequiresLegacyFieldAgreement()
    {
        var mirror = new StorageLayoutParameters("Mirror", 2, 16384, 2, 1);
        mirror.Validate(); // Structure is independent of the currently observed member set.
        Assert.Throws<ArgumentOutOfRangeException>(() => mirror.EstimateCapacity([100 * GiB, 100 * GiB]));
        var parity = new StorageLayoutParameters("Parity", 3, 16384, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => parity.EstimateCapacity([100 * GiB, 100 * GiB]));
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Request(OperationIntent.CreateVirtualDisk, [pool],
            Step("create", new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool), "VD", 10 * GiB, 65536, 1, Layout: mirror)))));
    }

    [Fact]
    public void CreatePoolAcceptsExactMultipleMembersAndRejectsDuplicateMissingCrossSystemOrWrongAnchor()
    {
        var first = Id(StorageObjectKind.PhysicalDisk, "first");
        var second = Id(StorageObjectKind.PhysicalDisk, "second");
        var firstRef = RealTargetReference.ForExisting(first);
        var secondRef = RealTargetReference.ForExisting(second);
        var command = new CreatePoolCommand(firstRef, "Pool", [firstRef, secondRef]);
        RealOperationIntentRequest Proposal(CreatePoolCommand value) => Request(OperationIntent.CreateStoragePool, [first, second], Step("pool", value));
        RealOperationValidator.Validate(Proposal(command));
        foreach (var invalid in new[]
                 {
                     command with { Members = [firstRef, firstRef] },
                     command with { Members = [firstRef] },
                     command with { Members = [secondRef, firstRef] },
                     command with { Members = [firstRef, RealTargetReference.ForExisting(new StorageObjectId(SystemId.New(), StorageObjectKind.PhysicalDisk, "foreign"))] },
                     command with { Members = [firstRef, RealTargetReference.ForExisting(Id(StorageObjectKind.PhysicalDisk, "unlisted"))] }
                 })
            Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(invalid)));
    }

    [Theory]
    [InlineData("HDD")]
    [InlineData("SSD")]
    [InlineData("SCM")]
    public void TierTemplatesAcceptSupportedMediaAndExplicitLayout(string media)
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var layout = new StorageLayoutParameters("Mirror", 1, 16384, 2, 1);
        var command = new CreateTierCommand(RealTargetReference.ForExisting(pool), "Tier", 16384, 1, media, layout);
        RealOperationValidator.Validate(Request(OperationIntent.CreateStorageTier, [pool], Step("tier", command)));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Request(OperationIntent.CreateStorageTier, [pool],
            Step("tier", command with { MediaType = "Unknown" }))));
    }

    [Theory]
    [InlineData(PhysicalDiskUsage.AutoSelect)]
    [InlineData(PhysicalDiskUsage.HotSpare)]
    [InlineData(PhysicalDiskUsage.Retired)]
    public void MemberUsageIsClosedCanonicalAndIncludedInPoolCreationFlow(string usage)
    {
        var member = Id(StorageObjectKind.PhysicalDisk, "member");
        var second = Id(StorageObjectKind.PhysicalDisk, "second");
        var reference = RealTargetReference.ForExisting(member);
        var command = new SetPhysicalDiskUsageCommand(reference, usage);
        var standalone = Request(OperationIntent.SetPhysicalDiskUsage, [member], Step("usage", command));
        RealOperationValidator.Validate(standalone);
        Assert.Equal(RiskLevel.R4StorageStructureMutation, OperationSecurityCatalog.Get(standalone.Intent).MinimumRisk);
        var restored = JsonSerializer.Deserialize<RealStorageCommand>(JsonSerializer.Serialize<RealStorageCommand>(command));
        Assert.Equal(command, restored);
        RealOperationValidator.Validate(Request(OperationIntent.CreateStoragePool, [member, second],
            Step("pool", new CreatePoolCommand(reference, "Pool", [reference, RealTargetReference.ForExisting(second)])),
            Step("usage", command, "pool")));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(standalone with
        {
            Steps = [Step("usage", command with { Usage = "HotSpare" })]
        }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(standalone with
        {
            Steps = [Step("usage", command with { PhysicalDisk = RealTargetReference.ForExisting(Id(StorageObjectKind.OsDisk, "disk")) })]
        }));
    }

    [Fact]
    public void MixedTierCapacityPreservesExplicitLayerAndRejectsPolicyOrTotalMismatch()
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var hdd = Id(StorageObjectKind.StorageTier, "hdd");
        var ssd = Id(StorageObjectKind.StorageTier, "ssd");
        var hddRef = RealTargetReference.ForExisting(hdd);
        var ssdRef = RealTargetReference.ForExisting(ssd);
        var hddLayout = new StorageLayoutParameters("Parity", 3, 16384, 1, 1);
        var ssdLayout = new StorageLayoutParameters("Mirror", 1, 32768, 2, 1);
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, 100 * GiB,
            MaximumCapacityAlgorithm.InitialCandidateBytes(100 * GiB), 300 * GiB, 204, "provider", "fingerprint", DateTimeOffset.UtcNow);
        var layers = new[]
        {
            new MaximumCapacityTier(hddRef, policy, null, true, hddLayout, "HDD"),
            new MaximumCapacityTier(ssdRef, null, 10 * GiB, false, ssdLayout, "SSD")
        };
        var command = new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool), hddRef, "VD", 0, true, CapacityTiers: layers);
        RealOperationIntentRequest Proposal(CreateTieredVirtualDiskCommand value) => Request(OperationIntent.CreateVirtualDisk, [pool, hdd, ssd], Step("create", value));
        RealOperationValidator.Validate(Proposal(command));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(command with
        {
            CapacityTiers = [layers[0], layers[1] with { MaximumCapacity = policy }]
        })));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(command with
        {
            CapacityTiers = [layers[0], layers[1] with { SizeBytes = 0 }]
        })));
        var explicitLayers = new[] { layers[0] with { MaximumCapacity = null, UseMaximumSize = false, SizeBytes = 20 * GiB }, layers[1] };
        var explicitCommand = command with { UseMaximumSize = false, SizeBytes = 30 * GiB, CapacityTiers = explicitLayers };
        RealOperationValidator.Validate(Proposal(explicitCommand));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(explicitCommand with { SizeBytes = 31 * GiB })));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(command with { Tier = ssdRef })));
    }

    [Fact]
    public void SingleTierArrayAcceptsExplicitCapacityAndAllReferencesRemainDeclared()
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var tier = Id(StorageObjectKind.StorageTier, "tier");
        var tierRef = RealTargetReference.ForExisting(tier);
        var layout = new StorageLayoutParameters("Simple", 1, 16384);
        var command = new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool), tierRef, "VD", 10 * GiB,
            CapacityTiers: [new MaximumCapacityTier(tierRef, null, 10 * GiB, false, layout, "SCM")], Layout: layout);
        var request = Request(OperationIntent.CreateVirtualDisk, [pool, tier], Step("create", command));
        RealOperationValidator.Validate(request);
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(request with { Targets = [pool] }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(request with
        {
            Steps = [Step("create", command with { Layout = layout with { InterleaveBytes = 65536 } })]
        }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Request(OperationIntent.CreateVirtualDisk, [pool], Step("unknown", new UnknownCommand()))));
    }

    [Fact]
    public void NewNullableFieldsPreserveExactLegacyCommandJsonAndFallbacks()
    {
        var member = RealTargetReference.ForExisting(Id(StorageObjectKind.PhysicalDisk, "member"));
        var pool = RealTargetReference.ForExisting(Id(StorageObjectKind.StoragePool, "pool"));
        var tier = RealTargetReference.ForExisting(Id(StorageObjectKind.StorageTier, "tier"));
        var poolCommand = new CreatePoolCommand(member, "Pool");
        Assert.Equal("{\"$command\":\"create-pool\",\"PhysicalDisk\":" + JsonSerializer.Serialize(member) + ",\"Name\":\"Pool\"}",
            JsonSerializer.Serialize<RealStorageCommand>(poolCommand));
        Assert.Equal(new[] { member }, poolCommand.EffectiveMembers);
        var tierCommand = new CreateTierCommand(pool, "Tier", 65536, 1);
        Assert.Equal("{\"$command\":\"create-tier\",\"Pool\":" + JsonSerializer.Serialize(pool) + ",\"Name\":\"Tier\",\"InterleaveBytes\":65536,\"DataColumns\":1}",
            JsonSerializer.Serialize<RealStorageCommand>(tierCommand));
        Assert.Equal("HDD", tierCommand.EffectiveMediaType);
        Assert.Equal(StorageLayoutParameters.LegacyDefault, tierCommand.EffectiveLayout);
        Assert.Equal("{\"Tier\":" + JsonSerializer.Serialize(tier) + ",\"MaximumCapacity\":null}",
            JsonSerializer.Serialize(new MaximumCapacityTier(tier, null)));
        RealStorageCommand[] legacy =
        [
            new CreateVirtualDiskCommand(pool, "VD", 10 * GiB, 65536, 1),
            new CreateTieredVirtualDiskCommand(pool, tier, "VD", 0, true,
                CapacityTiers: [new MaximumCapacityTier(tier, null), new MaximumCapacityTier(RealTargetReference.ForExisting(Id(StorageObjectKind.StorageTier, "tier-2")), null)])
        ];
        foreach (var command in legacy)
        {
            var json = JsonSerializer.Serialize(command);
            foreach (var field in new[] { "Effective", "Layout", "Members", "MediaType" }) Assert.DoesNotContain(field, json);
            Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<RealStorageCommand>(json)));
        }
    }

    [Fact]
    public void FrozenMemberCollectionIsCopiedAndNewLayoutRoleAndCapacityFieldsParticipateInHashes()
    {
        var first = Id(StorageObjectKind.PhysicalDisk, "first");
        var second = Id(StorageObjectKind.PhysicalDisk, "second");
        var inputs = new[] { RealTargetReference.ForExisting(first), RealTargetReference.ForExisting(second) };
        var request = Request(OperationIntent.CreateStoragePool, [first, second], Step("pool", new CreatePoolCommand(inputs[0], "Pool", inputs)));
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product", "process", 1234, now.AddMinutes(-1), "C:\\WinPool\\App.exe", true);
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        var plan = RealOperationPlanFactory.Create(request, OperationId.New(), environment, session, "inventory", "target",
            "members", "provider", now, now.AddMinutes(1));
        inputs[1] = inputs[0];
        Assert.Equal(plan.PlanHash, OperationPlanHasher.Compute(plan));
        Assert.Equal(second, Assert.IsType<CreatePoolCommand>(Assert.Single(plan.RealOperation!.Steps).Command).EffectiveMembers[1].Existing);

        string Hash(RealOperationIntentRequest proposal) => RealOperationIntentHasher.Compute(proposal, session, "machine");
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var layout = new StorageLayoutParameters("Simple", 1, 16384);
        var command = new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool), "VD", 10 * GiB, 16384, 1, Layout: layout);
        var ordinary = Request(OperationIntent.CreateVirtualDisk, [pool, first], Step("create", command));
        Assert.NotEqual(Hash(ordinary), Hash(ordinary with
        {
            Steps = [Step("create", command with { InterleaveBytes = 32768, Layout = layout with { InterleaveBytes = 32768 } })]
        }));
        var usage = Request(OperationIntent.SetPhysicalDiskUsage, [first], Step("usage", new SetPhysicalDiskUsageCommand(RealTargetReference.ForExisting(first), PhysicalDiskUsage.HotSpare)));
        Assert.NotEqual(Hash(usage), Hash(usage with
        {
            Steps = [Step("usage", new SetPhysicalDiskUsageCommand(RealTargetReference.ForExisting(first), PhysicalDiskUsage.Retired))]
        }));
        var tier = Id(StorageObjectKind.StorageTier, "tier");
        var tierRef = RealTargetReference.ForExisting(tier);
        var layer = new MaximumCapacityTier(tierRef, null, 10 * GiB, false, layout, "HDD");
        var tieredCommand = new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool), tierRef, "VD", 10 * GiB, CapacityTiers: [layer]);
        var tiered = Request(OperationIntent.CreateVirtualDisk, [pool, first, tier], Step("create", tieredCommand));
        Assert.NotEqual(Hash(tiered), Hash(tiered with
        {
            Steps = [Step("create", tieredCommand with { SizeBytes = 11 * GiB, CapacityTiers = [layer with { SizeBytes = 11 * GiB }] })]
        }));
    }

    private sealed record UnknownCommand : RealStorageCommand;
}
