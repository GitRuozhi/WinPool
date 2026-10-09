using System.Collections.Immutable;
using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;

namespace WinPool.App.Tests;

public sealed class RealStructureDraftPlannerTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    [Theory]
    [InlineData("replacement-explicit")]
    [InlineData("continuation")]
    [InlineData("delete-vd")]
    public void NativeSingleHddTierWithReturnedNullAggregateSupportsTheOriginalWorkflow(string action)
    {
        var session = NativeTierSession();
        var oldPool = session.Baseline.StoragePools.Single(pool => !pool.IsPrimordial);
        var vd = Assert.Single(session.Baseline.VirtualDisks);
        Assert.Null(vd.NumberOfColumns);
        Assert.Null(vd.Interleave);
        Assert.NotEqual("Simple", vd.ResiliencySettingName);
        Assert.NotEqual("Fixed", vd.ProvisioningType);
        Assert.Equal(32 * GiB, Assert.Single(session.Baseline.StorageTiers, tier => tier.VirtualDiskStableId == vd.StableId).Size);
        PrepareNativeTierAction(session, action);
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        if (action == "replacement-explicit")
        {
            Assert.Equal(oldPool.StableId, Assert.Single(plan.RemovedPools).StableId);
            var replacement = Assert.Single(plan.Creations);
            Assert.Equal(PoolVirtualDiskLayout.HddTiered, replacement.Intent.Layout);
            Assert.False(replacement.Intent.VirtualDiskUseMaximum);
            Assert.Equal(32 * GiB, replacement.Intent.VirtualDiskSizeBytes);
            Assert.Equal(oldPool.MemberPhysicalDiskIds[0], replacement.PhysicalDiskId);
            Assert.Empty(plan.RemovedVirtualDisks);
        }
        else if (action == "continuation")
        {
            var remaining = Assert.Single(plan.Creations);
            Assert.True(remaining.ContinueLayout);
            Assert.False(remaining.CreatePool);
            Assert.False(remaining.CreateVirtualDisk);
            Assert.Equal(vd.StableId, remaining.Intent.VerifiedVirtualDiskId);
        }
        else Assert.Equal(vd.StableId, Assert.Single(plan.RemovedVirtualDisks).StableId);
        // Preview forks preserve the same original source observations, including returned nulls.
        var preview = session.ForkPreview(session.Capture());
        Assert.Same(session.BaselineSourceFacts, preview.BaselineSourceFacts);
        Assert.True(RealStructureDraftPlanner.Build(preview).Preview.CanApply);
        var aggregate = session.BaselineSourceFacts!.Objects.Single(item => item.Id == vd.StableId).Field("ProvisioningType")!;
        Assert.Equal(FieldReadState.Returned, aggregate.ReadState);
        Assert.Null(aggregate.Value); // Persisted JSON value:null decodes as nullable JsonElement null.
    }

    [Theory]
    [InlineData("missing-aggregate")]
    [InlineData("failed-aggregate")]
    [InlineData("conflicting-aggregate")]
    [InlineData("thin-tier")]
    [InlineData("failed-tier-provisioning")]
    [InlineData("tier-interleave")]
    [InlineData("tier-columns")]
    [InlineData("tier-copies")]
    [InlineData("tier-media")]
    [InlineData("tier-size")]
    [InlineData("tier-allocation")]
    [InlineData("vd-allocation")]
    [InlineData("retained-parent")]
    [InlineData("missing-parent")]
    [InlineData("conflicting-parent")]
    [InlineData("different-member")]
    [InlineData("multiple-tiers")]
    [InlineData("unreliable-tier")]
    public void IncompleteOrUnsupportedActualTierBlocksBothReplacementAndContinuation(string defect)
    {
        var original = NativeTierSession();
        var facts = original.BaselineSourceFacts!;
        var vdId = original.Baseline.VirtualDisks.Single().StableId;
        var tierId = original.Baseline.StorageTiers.Single(tier => tier.VirtualDiskStableId == vdId).StableId;
        var memberId = original.Baseline.PhysicalDisks.Single().StableId;
        WinPoolFacts ChangeField(string id, string name, object value, FieldReadState state = FieldReadState.Returned) => facts with
        { Objects = facts.Objects.Select(item => item.Id != id ? item : item with
            { Fields = item.Fields.Select(field => field.Name != name ? field : field with
                { Value = state == FieldReadState.Returned ? JsonSerializer.SerializeToElement(value) : null,
                    ReadState = state }).ToImmutableArray() }).ToImmutableArray() };
        facts = defect switch
        {
            "missing-aggregate" => facts with { Objects = facts.Objects.Select(item => item.Id != vdId ? item : item with
                { Fields = item.Fields.Where(field => field.Name != "ProvisioningType").ToImmutableArray() }).ToImmutableArray() },
            "failed-aggregate" => ChangeField(vdId, "ProvisioningType", 2, FieldReadState.Failed),
            "conflicting-aggregate" => ChangeField(vdId, "NumberOfColumns", 2),
            "thin-tier" => ChangeField(tierId, "ProvisioningType", 1),
            "failed-tier-provisioning" => ChangeField(tierId, "ProvisioningType", 2, FieldReadState.Unavailable),
            "tier-interleave" => ChangeField(tierId, "Interleave", 262144),
            "tier-columns" => ChangeField(tierId, "NumberOfColumns", 2),
            "tier-copies" => ChangeField(tierId, "NumberOfDataCopies", 2),
            "tier-media" => ChangeField(tierId, "MediaType", 4),
            "tier-size" => ChangeField(tierId, "Size", 31 * GiB),
            "tier-allocation" => ChangeField(tierId, "AllocatedSize", 31 * GiB),
            "vd-allocation" => ChangeField(vdId, "AllocatedSize", 31 * GiB),
            "retained-parent" => facts with { Relationships = facts.Relationships.Select(edge =>
                edge.Kind == "virtual-disk-tier" ? edge with { IsRetained = true } : edge).ToImmutableArray() },
            "missing-parent" => facts with { Relationships = facts.Relationships.Where(edge => edge.Kind != "pool-tier").ToImmutableArray() },
            "conflicting-parent" => facts with { Relationships = facts.Relationships.Add(new(memberId, tierId, "virtual-disk-tier")) },
            "different-member" => facts with { Relationships = facts.Relationships.Select(edge =>
                edge.Kind == "tier-member" && edge.FromId == tierId ? edge with { ToId = vdId } : edge).ToImmutableArray() },
            "multiple-tiers" => facts with { Objects = facts.Objects.Add(facts.Objects.Single(item => item.Id == tierId) with { Id = tierId + ":second" }),
                Relationships = facts.Relationships.Where(edge => edge.FromId == tierId || edge.ToId == tierId)
                    .Aggregate(facts.Relationships, (edges, edge) => edges.Add(edge with
                    { FromId = edge.FromId == tierId ? tierId + ":second" : edge.FromId, ToId = edge.ToId == tierId ? tierId + ":second" : edge.ToId })) },
            "unreliable-tier" => facts with { Objects = facts.Objects.Select(item => item.Id == tierId
                ? item with { HasReliableIdentity = false } : item).ToImmutableArray() },
            _ => throw new ArgumentException(defect)
        };
        foreach (var action in new[] { "replacement-explicit", "continuation" })
        {
            var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:structure-draft-tests",
                StorageSystemKind.Local, "Native tier shape", facts, [], DateTimeOffset.UtcNow);
            var session = new SimulationEditingSession();
            session.Bind(document, document.Snapshot);
            PrepareNativeTierAction(session, action);
            var plan = RealStructureDraftPlanner.Build(session);
            Assert.False(plan.Preview.CanApply, defect + ": " + action);
            Assert.NotEmpty(plan.Preview.BlockingReasons);
            Assert.Empty(plan.RemovedPools);
            Assert.Empty(plan.RemovedVirtualDisks);
            Assert.Empty(plan.RemovedTiers);
            Assert.Empty(plan.Creations);
        }
    }

    private static void PrepareNativeTierAction(SimulationEditingSession session, string action)
    {
        var pool = session.Baseline.StoragePools.Single(item => !item.IsPrimordial);
        var vd = session.Baseline.VirtualDisks.Single();
        if (action == "replacement-explicit")
        {
            session.Working = SimulationEditingSession.RemoveRealPoolDraft(session.Working, pool.StableId);
            AddDraft(session, PoolVirtualDiskLayout.HddTiered, maximum: false);
        }
        else if (action == "delete-vd") session.Working = SimulationEditingSession.RemoveRealVirtualDiskDraft(session.Working, vd.StableId);
        else session.PoolIntents[pool.StableId] = Intent() with
        { Layout = PoolVirtualDiskLayout.HddTiered, VerifiedVirtualDiskId = vd.StableId, PendingAutomaticLayout = true };
    }

    private static SimulationEditingSession NativeTierSession()
    {
        var snapshot = ExistingSnapshot(withVirtualDisk: true, withTemplate: true);
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var vd = snapshot.VirtualDisks.Single() with { Size = 32 * GiB, FootprintOnPool = 32 * GiB, AllocatedSize = 32 * GiB };
        var actual = snapshot.StorageTiers.Single() with { Size = 32 * GiB, FootprintOnPool = 32 * GiB };
        var template = actual with { StableId = "tier:unused-template", VirtualDiskStableId = null, Size = 0, FootprintOnPool = 0 };
        snapshot = snapshot with { VirtualDisks = [vd], StorageTiers = [actual, template] };
        var document = Document(snapshot);
        var facts = WinPoolSimulationFacts.Create(snapshot, document.SystemId);
        facts = facts with
        {
            IsSimulation = false,
            Sources = facts.Sources.Select(source => source with { Origin = FactOrigin.StorageCim }).ToImmutableArray(),
            Objects = facts.Objects.Select(item => item.Id == vd.StableId ? item with
            {
                Fields = item.Fields.Select(field => field.Name is "ResiliencySettingName" or "ProvisioningType"
                    or "NumberOfColumns" or "Interleave" or "NumberOfDataCopies" or "PhysicalDiskRedundancy"
                    ? field with { Value = JsonSerializer.SerializeToElement<object?>(null) } : field).ToImmutableArray()
            } : item.Id == actual.StableId ? item with
            {
                Fields = item.Fields.Add(WinPoolSourceField.Returned("ProvisioningType", 2, FactValueType.UInt64, item.SourceRef))
                    .Add(WinPoolSourceField.Returned("AllocatedSize", 32 * GiB, FactValueType.UInt64, item.SourceRef, "bytes"))
            } : item).ToImmutableArray()
        };
        document = document with { SourceFacts = facts };
        document = JsonSerializer.Deserialize<StorageSystemDocument>(JsonSerializer.Serialize(document))!;
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        return session;
    }

    [Theory]
    [InlineData(PoolVirtualDiskLayout.Ordinary, false, 2 * MiB)]
    [InlineData(PoolVirtualDiskLayout.Ordinary, true, 18 * MiB)]
    [InlineData(PoolVirtualDiskLayout.Ordinary, false, 32 * MiB + 512)]
    [InlineData(PoolVirtualDiskLayout.HddTiered, false, 2 * MiB)]
    [InlineData(PoolVirtualDiskLayout.HddTiered, true, 18 * MiB)]
    [InlineData(PoolVirtualDiskLayout.HddTiered, true, 32 * MiB + 512)]
    public void ExplicitInvalidAutomaticCapacityBlocksReplacementBeforeAnyExecutableDeletion(
        PoolVirtualDiskLayout layout, bool createMsr, long bytes)
    {
        var session = Session(ExistingSnapshot(withVirtualDisk: true));
        var oldPool = session.Baseline.StoragePools.Single(pool => !pool.IsPrimordial);
        session.Working = SimulationEditingSession.RemoveRealPoolDraft(session.Working, oldPool.StableId);
        AddDraft(session, layout, maximum: false);
        var pool = session.Working.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        session.PoolIntents[pool.StableId] = session.PoolIntents[pool.StableId] with
        { VirtualDiskSizeBytes = bytes, CreateMsr = createMsr };
        var prior = session.Capture();

        var blocked = RealStructureDraftPlanner.Build(session);
        Assert.False(blocked.Preview.CanApply);
        Assert.Contains(blocked.Preview.BlockingReasons,
            reason => reason.Contains("automatic layout", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(blocked.RemovedPools);
        Assert.Empty(blocked.RemovedVirtualDisks);
        Assert.Empty(blocked.RemovedTiers);
        Assert.Empty(blocked.Creations);
        Assert.Same(prior.Snapshot, session.Working);
        Assert.Equal(prior.PoolIntents[pool.StableId], session.PoolIntents[pool.StableId]);

        session.PoolIntents[pool.StableId] = session.PoolIntents[pool.StableId] with { AutoCreatePartition = false };
        var withoutLayout = RealStructureDraftPlanner.Build(session);
        Assert.True(withoutLayout.Preview.CanApply, string.Join("; ", withoutLayout.Preview.BlockingReasons));
        Assert.Equal(oldPool.StableId, Assert.Single(withoutLayout.RemovedPools).StableId);

        // Both layouts preserve MAX for the Agent to freeze the shared GiB search.
        session.PoolIntents[pool.StableId] = session.PoolIntents[pool.StableId] with
        { AutoCreatePartition = true, VirtualDiskUseMaximum = true, VirtualDiskSizeBytes = null };
        var maximumChinese = RealStructureDraftPlanner.Build(session, chinese: true);
        Assert.True(maximumChinese.Preview.CanApply, string.Join("; ", maximumChinese.Preview.BlockingReasons));
        Assert.Contains(maximumChinese.Preview.Actions, action => action.Contains("1 GiB", StringComparison.Ordinal));
        var maximumEnglish = RealStructureDraftPlanner.Build(session, chinese: false);
        Assert.True(maximumEnglish.Preview.CanApply);
        Assert.Contains(maximumEnglish.Preview.Actions, action => action.Contains("1 GiB steps", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PoolVirtualDiskLayout.Ordinary, false)]
    [InlineData(PoolVirtualDiskLayout.Ordinary, true)]
    [InlineData(PoolVirtualDiskLayout.HddTiered, false)]
    public void OriginalDraftCarriesNamesLayoutExactSizeOrMaximumAndPartitionChoices(
        PoolVirtualDiskLayout layout, bool maximum)
    {
        var session = Draft(layout, maximum);
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        var creation = Assert.Single(plan.Creations);
        Assert.True(creation.CreatePool);
        Assert.True(creation.CreateVirtualDisk);
        Assert.Equal("User pool", creation.Pool.FriendlyName);
        Assert.Equal("User VD", creation.Intent.VirtualDiskName);
        Assert.Equal("User label", creation.Intent.VolumeName);
        Assert.Equal(layout, creation.Intent.Layout);
        Assert.Equal(maximum, creation.Intent.VirtualDiskUseMaximum);
        Assert.Equal(maximum ? (long?)null : 32L * GiB, creation.Intent.VirtualDiskSizeBytes);
        Assert.False(creation.Intent.CreateMsr);
        Assert.Equal('E', creation.Intent.DriveLetter);
        Assert.Equal("NTFS", creation.Intent.FileSystem);
        Assert.Equal(65536, creation.Intent.AllocationUnitSize);
        Assert.DoesNotContain(plan.Preview.Actions, action => action.Contains("MovePhysicalDisk", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyRealPoolDraftRetainsItsInputAndHasNoCreationPlan()
    {
        var session = Session(FreeSnapshot());
        session.Working = EditWorkspace.InsertDraftPool(session.Working, "No members");
        var pool = session.Working.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        session.PoolIntents[pool.StableId] = Intent();
        var prior = session.Capture();
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEmpty(plan.Preview.BlockingReasons);
        Assert.Empty(plan.Creations);
        Assert.Same(prior.Snapshot, session.Working);
        Assert.Equal(prior.PoolIntents[pool.StableId], session.PoolIntents[pool.StableId]);
    }

    [Theory]
    [InlineData(PoolVirtualDiskLayout.Ordinary, true)]
    [InlineData(PoolVirtualDiskLayout.HddTiered, false)]
    public void DissolveAndNewPoolOnSameMemberPreservesOrdinaryMaximumAndTieredExplicitReplacement(
        PoolVirtualDiskLayout layout, bool maximum)
    {
        var before = ExistingSnapshot(withVirtualDisk: true);
        var session = Session(before);
        var old = before.StoragePools.Single(pool => !pool.IsPrimordial);
        session.Working = SimulationEditingSession.RemoveRealPoolDraft(session.Working, old.StableId);
        AddDraft(session, layout, maximum);
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        Assert.Equal(old.StableId, Assert.Single(plan.RemovedPools).StableId);
        Assert.Empty(plan.RemovedVirtualDisks); // Parent deletion owns its exact children once.
        var replacement = Assert.Single(plan.Creations);
        Assert.Equal(old.MemberPhysicalDiskIds[0], replacement.PhysicalDiskId);
        Assert.Equal(layout, replacement.Intent.Layout);
        Assert.Equal(maximum, replacement.Intent.VirtualDiskUseMaximum);
        Assert.Contains(plan.Preview.Actions, action => action.Contains("all data", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewHddTieredMaximumKeepsDraftAndCreatesAnAgentSearchIntent(bool replacingExistingPool)
    {
        var session = Session(replacingExistingPool
            ? ExistingSnapshot(withVirtualDisk: true)
            : FreeSnapshot());
        if (replacingExistingPool)
        {
            var oldPool = session.Baseline.StoragePools.Single(pool => !pool.IsPrimordial);
            session.Working = SimulationEditingSession.RemoveRealPoolDraft(session.Working, oldPool.StableId);
        }
        AddDraft(session, PoolVirtualDiskLayout.HddTiered, maximum: true);
        var prior = session.Capture();

        var plan = RealStructureDraftPlanner.Build(session);

        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        Assert.Contains(plan.Preview.Actions, action => action.Contains("1 GiB steps", StringComparison.Ordinal));
        Assert.Equal(replacingExistingPool ? 1 : 0, plan.RemovedPools.Count);
        var creation = Assert.Single(plan.Creations);
        Assert.True(creation.Intent.VirtualDiskUseMaximum);
        Assert.Equal(PoolVirtualDiskLayout.HddTiered, creation.Intent.Layout);
        Assert.Same(prior.Snapshot, session.Working);
    }

    [Fact]
    public void DirectMigrationWithoutDissolutionIsRejectedBeforeAnyExecutablePlan()
    {
        var session = Session(ExistingSnapshot());
        AddDraft(session, PoolVirtualDiskLayout.Ordinary, maximum: true);
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.Contains(plan.Preview.BlockingReasons, reason => reason.Contains("migrat", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("membership", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftwareAutoVdPreferenceCannotCreateADeletedOrAbsentDraftVd(bool preference)
    {
        var session = Draft(PoolVirtualDiskLayout.Ordinary, maximum: true);
        var pool = session.Working.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        session.Working = session.Working with { VirtualDisks = [] };
        session.PoolIntents[pool.StableId] = session.PoolIntents[pool.StableId] with { AutoCreateVirtualDisk = preference };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply);
        Assert.False(Assert.Single(plan.Creations).CreateVirtualDisk);
        Assert.DoesNotContain(plan.Preview.Actions, action => action.StartsWith("Create ordinary VD", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingEmptyPoolFirstVdDoesNotRecreateThePoolOrAddAutomaticLayoutWhenOff()
    {
        var session = Session(ExistingSnapshot());
        var pool = session.Working.StoragePools.Single(item => !item.IsPrimordial);
        session.Working = SimulationEditingSession.InsertRealDraftVirtualDisk(session.Working, pool.StableId, "First");
        session.PoolIntents[pool.StableId] = Intent() with { AutoCreateVirtualDisk = false, AutoCreatePartition = false };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply);
        var creation = Assert.Single(plan.Creations);
        Assert.False(creation.CreatePool);
        Assert.True(creation.CreateVirtualDisk);
        Assert.DoesNotContain(plan.Preview.Actions, action => action.Contains("Automatic GPT", StringComparison.Ordinal));
    }

    [Fact]
    public void SoleVdDeletionIsStagedAndDoesNotBecomePoolDeletionOrRecreation()
    {
        var session = Session(ExistingSnapshot(withVirtualDisk: true));
        var vd = Assert.Single(session.Working.VirtualDisks);
        session.Working = SimulationEditingSession.RemoveRealVirtualDiskDraft(session.Working, vd.StableId);
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply);
        Assert.Equal(vd.StableId, Assert.Single(plan.RemovedVirtualDisks).StableId);
        Assert.Empty(plan.RemovedPools);
        Assert.Empty(plan.Creations);
    }

    [Fact]
    public void UnusedAccurateTemplateDeletionIsOneStagedAction()
    {
        var session = Session(ExistingSnapshot(withTemplate: true));
        var tier = Assert.Single(session.Working.StorageTiers);
        session.Working = session.Working with { StorageTiers = [] };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply);
        Assert.Equal(tier.StableId, Assert.Single(plan.RemovedTiers).StableId);
        Assert.Empty(plan.RemovedPools);
        Assert.Empty(plan.Creations);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("identity")]
    [InlineData("columns")]
    public void UnsupportedTemplateCannotBeDeletedThroughAnApparentlyEligiblePool(string defect)
    {
        var before = ExistingSnapshot(withTemplate: true);
        before = before with { StorageTiers = before.StorageTiers.Select(tier => defect switch
        {
            "media" => tier with { MediaType = "SSD" },
            "identity" => tier with { IsStable = false },
            _ => tier with { NumberOfColumns = 2 }
        }).ToArray() };
        var document = Document(before);
        if (defect == "identity") document = document with
        {
            SourceFacts = document.SourceFacts! with
            {
                Objects = document.SourceFacts.Objects.Select(item => item.ObjectType == FactObjectType.StorageTier
                    ? item with { HasReliableIdentity = false } : item).ToImmutableArray()
            }
        };
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        session.Working = session.Working with { StorageTiers = [] };
        Assert.False(RealStructureDraftPlanner.Build(session).Preview.CanApply);
    }

    [Theory]
    [InlineData("tier-copies")]
    [InlineData("tier-parent")]
    [InlineData("vd-tier-association")]
    [InlineData("vd-name")]
    [InlineData("pool-size")]
    public void UnsupportedExistingChangesHaveExplicitBlockingReasonsRatherThanBeingIgnored(string change)
    {
        var session = Session(ExistingSnapshot(withVirtualDisk: true, withTemplate: true));
        session.Working = change switch
        {
            "tier-copies" => session.Working with { StorageTiers = session.Working.StorageTiers.Select(t => t with { NumberOfDataCopies = 2 }).ToArray() },
            "tier-parent" => session.Working with { StorageTiers = session.Working.StorageTiers.Select(t => t with { PoolStableId = "another-pool" }).ToArray() },
            "vd-tier-association" => session.Working with { VirtualDisks = session.Working.VirtualDisks.Select(v => v with { TierStableIds = ["unknown-tier"] }).ToArray() },
            "vd-name" => session.Working with { VirtualDisks = session.Working.VirtualDisks.Select(v => v with { FriendlyName = "Draft rename" }).ToArray() },
            _ => session.Working with { StoragePools = session.Working.StoragePools.Select(p => p.IsPrimordial ? p : p with { Size = p.Size - GiB }).ToArray() }
        };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEmpty(plan.Preview.BlockingReasons);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreationNeverTreatsAnUnknownSourceIdentityAsADraftOutput(bool replacePoolId)
    {
        var session = Draft(PoolVirtualDiskLayout.Ordinary, maximum: true);
        var pool = session.Working.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        if (replacePoolId)
        {
            const string unexpected = "source:unobserved-pool";
            session.Working = session.Working with
            {
                StoragePools = session.Working.StoragePools.Select(p => p.StableId == pool.StableId ? p with { StableId = unexpected } : p).ToArray(),
                VirtualDisks = session.Working.VirtualDisks.Select(v => v with { PoolStableId = unexpected }).ToArray()
            };
            session.PoolIntents[unexpected] = session.PoolIntents[pool.StableId];
        }
        else session.Working = session.Working with
        {
            VirtualDisks = session.Working.VirtualDisks.Select(v => v with { StableId = "source:unobserved-vd", IsStable = true }).ToArray()
        };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEmpty(plan.Preview.BlockingReasons);
    }

    [Fact]
    public void VerifiedPoolCreationResumesWithTheRealPoolIdentityAndOriginalRemainingParameters()
    {
        var session = Draft(PoolVirtualDiskLayout.HddTiered, maximum: false);
        var draftPool = session.Working.StoragePools.Single(p => EditWorkspace.IsDraftPool(p.StableId));
        var intent = session.PoolIntents[draftPool.StableId];
        var freshDocument = Document(ExistingSnapshot());
        var fresh = freshDocument.Snapshot;
        var realPool = fresh.StoragePools.Single(p => !p.IsPrimordial);
        var remaining = session.Capture() with
        {
            Snapshot = fresh with { VirtualDisks = session.Working.VirtualDisks.Select(v => v with { PoolStableId = realPool.StableId }).ToArray() },
            PoolIntents = new Dictionary<string, PoolEditIntent> { [realPool.StableId] = intent }
        };
        session.RebaseVerified(freshDocument with { Revision = 1 }, remaining);
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        var creation = Assert.Single(plan.Creations);
        Assert.False(creation.CreatePool);
        Assert.Equal(realPool.StableId, creation.Pool.StableId);
        Assert.Equal(intent, creation.Intent);
        Assert.DoesNotContain(plan.Creations, c => c.Pool.StableId == draftPool.StableId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictOrUnknownPreservesTargetButBlocksAllWritePreparation(bool unknown)
    {
        var session = Draft(PoolVirtualDiskLayout.Ordinary, maximum: true);
        var prior = session.Capture();
        if (unknown) session.OutcomeUnknown = true;
        else session.MarkBaselineConflict(Document(session.Baseline) with { Revision = 1 });
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.Same(prior.Snapshot, session.Working);
        Assert.Equal(prior.PoolIntents.Single().Value, session.PoolIntents.Single().Value);
    }

    [Fact]
    public void VerifiedVdMarkerContinuesOnlyTheUnfinishedLayoutWithoutCreatingAnotherVd()
    {
        var session = Session(ExistingSnapshot(withVirtualDisk: true));
        var pool = session.Working.StoragePools.Single(p => !p.IsPrimordial);
        var vd = Assert.Single(session.Working.VirtualDisks);
        session.PoolIntents[pool.StableId] = Intent() with
        { VerifiedVirtualDiskId = vd.StableId, PendingAutomaticLayout = true };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        var continuation = Assert.Single(plan.Creations);
        Assert.True(continuation.ContinueLayout);
        Assert.False(continuation.CreatePool);
        Assert.False(continuation.CreateVirtualDisk);
        Assert.Equal(vd.StableId, continuation.Intent.VerifiedVirtualDiskId);
        Assert.Contains(plan.Preview.Actions, action => action.StartsWith("Automatic GPT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-vd")]
    [InlineData("different-vd")]
    [InlineData("unsupported-format")]
    [InlineData("unsupported-letter")]
    public void UnverifiedOrUnsupportedRemainingLayoutCannotPrepareWrites(string defect)
    {
        var session = Session(ExistingSnapshot(withVirtualDisk: true));
        var pool = session.Working.StoragePools.Single(p => !p.IsPrimordial);
        var vd = Assert.Single(session.Working.VirtualDisks);
        var intent = Intent() with { VerifiedVirtualDiskId = vd.StableId, PendingAutomaticLayout = true };
        intent = defect switch
        {
            "missing-vd" => intent with { VerifiedVirtualDiskId = null },
            "different-vd" => intent with { VerifiedVirtualDiskId = "another:vd" },
            "unsupported-format" => intent with { FileSystem = "exFAT" },
            _ => intent with { DriveLetter = 'C' }
        };
        session.PoolIntents[pool.StableId] = intent;
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEmpty(plan.Preview.BlockingReasons);
    }

    [Fact]
    public void PagePreviewCandidateDoesNotConfuseReprojectedUnrelatedVolumesWithEdits()
    {
        var session = Session(SimulationLayouts.PrimordialReady());
        AddDraft(session, PoolVirtualDiskLayout.Ordinary, maximum: true);
        var candidate = new SimulationEditingSession();
        candidate.Bind(Document(session.Baseline).WithCandidate(session.Baseline), session.Working);
        candidate.Restore(session.Capture());
        var plan = RealStructureDraftPlanner.Build(candidate);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        Assert.Single(plan.Creations);
    }

    [Fact]
    public void NativePartitionRoleFactsSurvivePagePreviewWithoutReseedingTheBaseline()
    {
        var source = SimulationLayouts.PrimordialReady();
        var document = Document(source);
        document = document with { SourceFacts = WinPoolSimulationFacts.Create(source, document.SystemId) with { IsSimulation = false } };
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        Assert.Contains(session.Baseline.Partitions, partition => !string.IsNullOrWhiteSpace(partition.PartitionTypeId));
        AddDraft(session, PoolVirtualDiskLayout.Ordinary, maximum: true);
        var candidate = session.ForkPreview(session.Capture());
        Assert.Same(session.Baseline, candidate.Baseline);
        Assert.Equal(session.SystemId, candidate.SystemId);
        Assert.Equal(session.BaselineRevision, candidate.BaselineRevision);
        Assert.True(RealStructureDraftPlanner.Build(candidate).Preview.CanApply);
        session.OutcomeUnknown = true;
        Assert.False(RealStructureDraftPlanner.Build(session.ForkPreview(session.Capture())).Preview.CanApply);
        session.OutcomeUnknown = false;
        session.MarkBaselineConflict(document with { Revision = 1 });
        Assert.False(RealStructureDraftPlanner.Build(session.ForkPreview(session.Capture())).Preview.CanApply);
    }

    [Theory]
    [InlineData("physical-role")]
    [InlineData("direct-disk")]
    [InlineData("partition")]
    [InlineData("volume")]
    [InlineData("duplicate-identity")]
    public void UnrelatedUnsupportedEditsCannotHideBehindASupportedPoolCreation(string change)
    {
        var session = Session(SimulationLayouts.PrimordialReady());
        AddDraft(session, PoolVirtualDiskLayout.Ordinary, maximum: true);
        session.Working = change switch
        {
            "physical-role" => session.Working with { PhysicalDisks = session.Working.PhysicalDisks.Select(d => d.IsSystem ? d with { IsSystem = false } : d).ToArray() },
            "direct-disk" => session.Working with { OsDisks = session.Working.OsDisks.Skip(1).ToArray() },
            "partition" => session.Working with { Partitions = session.Working.Partitions.Select(p => p with { Size = p.Size - 1024 * 1024 }).ToArray() },
            "volume" => session.Working with { Volumes = session.Working.Volumes.Select(v => v with { FileSystemLabel = "Not an Enter rename" }).ToArray() },
            _ => session.Working with { StoragePools = session.Working.StoragePools.Append(session.Working.StoragePools[0]).ToArray() }
        };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEmpty(plan.Preview.BlockingReasons);
    }

    [Fact]
    public void ExistingHddTemplateCannotSilentlyBecomeAnOrdinaryVd()
    {
        var session = Session(ExistingSnapshot(withTemplate: true));
        var pool = session.Working.StoragePools.Single(p => !p.IsPrimordial);
        session.Working = SimulationEditingSession.InsertRealDraftVirtualDisk(session.Working, pool.StableId, "First");
        session.PoolIntents[pool.StableId] = Intent() with { Layout = PoolVirtualDiskLayout.Ordinary };
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.Contains(plan.Preview.BlockingReasons, reason => reason.Contains("templates", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("orphan-vd")]
    [InlineData("primordial-identity")]
    [InlineData("duplicate-source")]
    [InlineData("ambiguous-owner")]
    public void InvalidSourceOrTargetRelationshipsNeverHideBehindAValidCreation(string defect)
    {
        var session = Draft(PoolVirtualDiskLayout.Ordinary, maximum: true);
        var before = session.Baseline;
        var target = session.Working;
        if (defect == "orphan-vd")
            session.Working = target with { VirtualDisks = target.VirtualDisks.Select(vd => vd with { PoolStableId = "absent:pool" }).ToArray() };
        else if (defect == "primordial-identity")
            session.Working = target with { StoragePools = target.StoragePools.Select(pool => pool.IsPrimordial ? pool with { StableId = "new:primordial" } : pool).ToArray() };
        else
        {
            var sourcePool = before.StoragePools.Single();
            var bad = defect == "duplicate-source"
                ? before with { PhysicalDisks = before.PhysicalDisks.Concat(before.PhysicalDisks).ToArray() }
                : before with { StoragePools = before.StoragePools.Concat([
                    sourcePool with { StableId = "pool:owner-a", IsPrimordial = false },
                    sourcePool with { StableId = "pool:owner-b", IsPrimordial = false }]).ToArray() };
            if (defect == "duplicate-source")
            {
                // Invalid source facts are rejected before an editing session can bind them.
                Assert.Throws<InvalidDataException>(() => Document(bad));
                return;
            }
            session.Bind(Document(bad), target);
            var pool = target.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
            session.PoolIntents[pool.StableId] = Intent();
        }
        var plan = RealStructureDraftPlanner.Build(session);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEmpty(plan.Preview.BlockingReasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalVerifiedRefreshSatisfiesPoolOnlyOrVdWithoutAutomaticPartitionGoals(bool virtualDisk)
    {
        var document = Document(ExistingSnapshot(withVirtualDisk: virtualDisk));
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        var pool = session.Working.StoragePools.Single(pool => !pool.IsPrimordial);
        session.PoolIntents[pool.StableId] = Intent() with
        {
            AutoCreatePartition = false, VerifiedVirtualDiskId = session.Working.VirtualDisks.FirstOrDefault()?.StableId,
            PendingAutomaticLayout = false
        };
        session.RebaseVerified(document with { Revision = 1 }, session.Capture());
        Assert.False(RealStructureDraftPlanner.Build(session).Preview.CanApply);
        Assert.True(RealStructureDraftPlanner.IsSatisfiedByVerifiedBaseline(session));
        session.OutcomeUnknown = true;
        Assert.False(RealStructureDraftPlanner.IsSatisfiedByVerifiedBaseline(session));
        session.OutcomeUnknown = false;
        session.MarkBaselineConflict(document with { Revision = 2 });
        Assert.False(RealStructureDraftPlanner.IsSatisfiedByVerifiedBaseline(session));
    }

    [Fact]
    public void VerifiedVdWithUnfinishedAutomaticLayoutDoesNotReportTheFinalGoalSatisfied()
    {
        var session = Session(ExistingSnapshot(withVirtualDisk: true));
        var pool = session.Working.StoragePools.Single(pool => !pool.IsPrimordial);
        session.PoolIntents[pool.StableId] = Intent() with
        { VerifiedVirtualDiskId = session.Working.VirtualDisks.Single().StableId, PendingAutomaticLayout = true };
        Assert.False(RealStructureDraftPlanner.IsSatisfiedByVerifiedBaseline(session));
    }

    private static SimulationEditingSession Draft(PoolVirtualDiskLayout layout, bool maximum)
    {
        var session = Session(FreeSnapshot());
        AddDraft(session, layout, maximum);
        return session;
    }

    private static void AddDraft(SimulationEditingSession session, PoolVirtualDiskLayout layout, bool maximum)
    {
        var member = session.Baseline.PhysicalDisks.First(disk => disk.MediaType == "HDD");
        session.Working = EditWorkspace.InsertDraftPool(session.Working, "User pool");
        var pool = session.Working.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        session.Working = EditWorkspace.MoveDiskToPool(session.Working, member.StableId, pool.StableId);
        session.Working = SimulationEditingSession.InsertRealDraftVirtualDisk(session.Working, pool.StableId, "User VD");
        session.PoolIntents[pool.StableId] = Intent() with
        {
            Layout = layout,
            VirtualDiskUseMaximum = maximum,
            VirtualDiskSizeBytes = maximum ? null : 32L * GiB
        };
    }

    private static PoolEditIntent Intent() => new(true, true, "NTFS", 65536, "User label",
        VirtualDiskName: "User VD", CreateMsr: false, DriveLetter: 'E');

    private static SimulationEditingSession Session(StorageSnapshot snapshot)
    {
        var session = new SimulationEditingSession();
        var document = Document(snapshot);
        session.Bind(document, document.Snapshot);
        return session;
    }

    private static StorageSystemDocument Document(StorageSnapshot snapshot) => new(
        StorageSystemDocument.CurrentSchemaVersion, "local:structure-draft-tests", StorageSystemKind.Local,
        "Draft tests", snapshot, [], DateTimeOffset.UtcNow);

    private static StorageSnapshot FreeSnapshot()
    {
        var source = SimulationLayouts.PrimordialReady();
        var member = source.PhysicalDisks.First(disk => disk.MediaType == "HDD");
        var primordial = source.StoragePools.Single(pool => pool.IsPrimordial) with
        { MemberPhysicalDiskIds = [member.StableId], Size = member.Size };
        return source with
        {
            PhysicalDisks = [member], StoragePools = [primordial], StorageTiers = [], VirtualDisks = [],
            OsDisks = source.OsDisks.Where(disk => disk.PhysicalDiskStableId == member.StableId).ToArray(),
            Partitions = [], Volumes = [], Relationships = []
        };
    }

    private static StorageSnapshot ExistingSnapshot(bool withVirtualDisk = false, bool withTemplate = false)
    {
        var source = FreeSnapshot();
        var member = source.PhysicalDisks.Single();
        const string poolId = "pool:existing";
        const string vdId = "vd:existing";
        var pool = source.StoragePools.Single() with
        { StableId = poolId, IsPrimordial = false, FriendlyName = "Existing", MemberPhysicalDiskIds = [member.StableId] };
        return source with
        {
            PhysicalDisks = [member with { PoolStableId = poolId, CanPool = false }],
            StoragePools = [source.StoragePools.Single() with { MemberPhysicalDiskIds = [], Size = 0 }, pool],
            VirtualDisks = withVirtualDisk ? [new VirtualDiskInfo(vdId, true, "Existing VD", "Healthy", "OK",
                "Simple", "Fixed", 1, 65536, 64L * GiB, 64L * GiB, poolId,
                withTemplate ? ["tier:existing"] : [], [])] : [],
            StorageTiers = withTemplate ? [new StorageTierInfo("tier:existing", true, "HDD template", "HDD", "Simple",
                0, 0, poolId, withVirtualDisk ? vdId : null, [member.StableId], 1, 65536, 1, 0)] : [],
            OsDisks = []
        };
    }
}
