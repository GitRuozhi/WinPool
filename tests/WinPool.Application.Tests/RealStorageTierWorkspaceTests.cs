using System.Collections.Immutable;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class RealStorageTierWorkspaceTests
{
    private const string PoolId = "pool:uid:56d5dc514f2ecc5eee30e869";
    private const string TemplateId = "tier:uid:f6df7c46e8586c776bec9f0a";
    private const string InstanceId = "tier:uid:allocated-instance";
    private const string PhysicalId = "physical:uid:6ceea032f3fb375a61cd27bd";
    private const string VirtualDiskId = "vdisk:uid:tiered";

    [Fact]
    public void ReturnedZeroSizeTemplateIsSelectableWithoutInventingAllocatedMembers()
    {
        var facts = ProviderFacts(withInstance: false);
        var tierObject = Assert.Single(facts.Objects, item => item.Id == TemplateId);
        Assert.Equal("MSFT_StorageTier", facts.Sources.Single(item => item.Id == tierObject.SourceRef).ClassName);
        Assert.Equal(FieldReadState.Returned, tierObject.Field("Size")!.ReadState);
        Assert.Contains(facts.Relationships, item => item.Kind == "template-pool-member"
            && item.FromId == TemplateId && item.ToId == PhysicalId);

        var snapshot = WinPoolStorageProjection.Project(facts);
        var tier = Assert.Single(snapshot.StorageTiers);
        Assert.Equal(TemplateId, tier.StableId);
        Assert.Null(tier.VirtualDiskStableId);
        Assert.Empty(tier.MemberPhysicalDiskIds);
        Assert.Equal(0, tier.Size);
        Assert.Equal(0, tier.FootprintOnPool);
        var poolNode = EditWorkspace.ProjectPoolWorkspaceRoot(snapshot, 0,
            showSourceTierTemplates: true).Children.Single(item => item.Unit.StableId == PoolId);
        var templateNode = Assert.Single(poolNode.Children, item => item.Unit.Kind == StorageUnitKind.StorageTier);
        Assert.Equal(TemplateId, templateNode.Unit.StableId);
        Assert.True(templateNode.IsSelectable);
        Assert.Equal(PoolId, templateNode.Unit.ParentStableId);
        Assert.Empty(templateNode.Children);
        var directGroup = Assert.Single(poolNode.Children, item => item.Unit.Kind == StorageUnitKind.DirectDiskGroup);
        Assert.Equal(PhysicalId, Assert.Single(directGroup.Children).Unit.StableId);
        Assert.Equal(TemplateId, EditWorkspace.SelectPoolTiersForForm(snapshot, PoolId,
            TemplateId, null, isSimulatedInventory: false)["HDD"].StableId);
    }

    [Fact]
    public void SameMediaTemplateAndInstanceRenderAndSelectTheirOwnStableIdentities()
    {
        var snapshot = WinPoolStorageProjection.Project(ProviderFacts(withInstance: true));
        var pool = EditWorkspace.ProjectPoolWorkspace(snapshot, 0, showSourceTierTemplates: true)
            .Single(item => item.Unit.StableId == PoolId);
        var tiers = pool.Children.Where(item => item.Unit.Kind == StorageUnitKind.StorageTier).ToArray();
        Assert.Equal(2, tiers.Length);
        Assert.All(tiers, item => Assert.True(item.IsSelectable));
        Assert.Empty(tiers.Single(item => item.Unit.StableId == TemplateId).Children);
        Assert.Equal(PhysicalId, Assert.Single(tiers.Single(item => item.Unit.StableId == InstanceId).Children).Unit.StableId);
        Assert.DoesNotContain(pool.Children, item => item.Unit.Kind == StorageUnitKind.DirectDiskGroup);

        var template = EditWorkspace.SelectPoolTiersForForm(snapshot, PoolId, TemplateId,
            VirtualDiskId, isSimulatedInventory: false)["HDD"];
        var instance = EditWorkspace.SelectPoolTiersForForm(snapshot, PoolId, InstanceId,
            null, isSimulatedInventory: false)["HDD"];
        Assert.Equal(TemplateId, template.StableId);
        Assert.Null(template.VirtualDiskStableId);
        Assert.Equal(0, template.Size);
        Assert.Empty(template.MemberPhysicalDiskIds);
        Assert.Equal(InstanceId, instance.StableId);
        Assert.Equal(VirtualDiskId, instance.VirtualDiskStableId);
        Assert.Equal(16L * 1024 * 1024 * 1024, instance.Size);
        Assert.Equal(PhysicalId, Assert.Single(instance.MemberPhysicalDiskIds));
        Assert.Equal(InstanceId, EditWorkspace.SelectPoolTiersForForm(snapshot, PoolId, null,
            VirtualDiskId, isSimulatedInventory: false)["HDD"].StableId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("tier:wrong", null)]
    [InlineData(null, "vdisk:wrong")]
    public void AmbiguousMediaDoesNotChooseATemplateOrInstanceByName(string? selectedTier, string? selectedDisk)
    {
        var snapshot = WinPoolStorageProjection.Project(ProviderFacts(withInstance: true));
        Assert.Empty(EditWorkspace.SelectPoolTiersForForm(snapshot, PoolId, selectedTier,
            selectedDisk, isSimulatedInventory: false));
    }

    [Fact]
    public void DefaultSimulationProjectionStillHidesAnEmptyTier()
    {
        var snapshot = WinPoolStorageProjection.Project(ProviderFacts(withInstance: false));
        var pool = EditWorkspace.ProjectPoolWorkspace(snapshot, 0).Single(item => item.Unit.StableId == PoolId);
        Assert.DoesNotContain(pool.Children, item => item.Unit.Kind == StorageUnitKind.StorageTier);
        Assert.Equal(TemplateId, EditWorkspace.SelectPoolTiersForForm(snapshot, PoolId, null,
            null, isSimulatedInventory: true)["HDD"].StableId);
    }

    private static WinPoolFacts ProviderFacts(bool withInstance)
    {
        var template = new StorageTierInfo(TemplateId, true, "WinPool_Stage1_Pool_HDD", "HDD", "Simple",
            0, 0, PoolId, null, [], NumberOfColumns: 1, Interleave: 65536);
        var instance = template with
        {
            StableId = InstanceId, VirtualDiskStableId = VirtualDiskId,
            Size = 16L * 1024 * 1024 * 1024, FootprintOnPool = 16L * 1024 * 1024 * 1024,
            MemberPhysicalDiskIds = [PhysicalId]
        };
        var snapshot = StorageSnapshot.Empty("TEST-PC") with
        {
            SnapshotVersion = "provider-tier-fresh", ScannedAt = DateTimeOffset.UtcNow,
            StorageSubsystems = [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            PhysicalDisks = [new PhysicalDiskInfo(PhysicalId, true, "WDC", "WD40EZAZ", "WD-WX22D610FP80",
                "SATA", "HDD", 4_000_787_030_016, 512, 4096, "Healthy", "OK", false, string.Empty,
                0, false, false, false, false, PoolId)],
            StoragePools = [new StoragePoolInfo(PoolId, true, "WinPool_Stage1_Pool", false,
                "Healthy", "OK", 4_000_000_000_000, withInstance ? instance.Size : 0, "subsystem:1", [PhysicalId])],
            StorageTiers = withInstance ? [template, instance] : [template],
            VirtualDisks = withInstance ? [new VirtualDiskInfo(VirtualDiskId, true, "Tiered VD", "Healthy", "OK",
                "Simple", "Fixed", 1, 65536, instance.Size, instance.Size, PoolId, [InstanceId], [])] : []
        };
        // Generate complete MSFT-shaped fields, then use returned provider origins
        // and its separate template eligibility relation. No allocated tier-member
        // edge is invented for the unused template.
        var facts = WinPoolSimulationFacts.Create(snapshot, SystemId.New());
        return facts with
        {
            IsSimulation = false,
            Sources = facts.Sources.Select(source => source with
            {
                Origin = source.Namespace == "root/microsoft/windows/storage" ? FactOrigin.StorageCim : FactOrigin.Native
            }).ToImmutableArray(),
            Relationships = facts.Relationships.Add(new WinPoolFactRelationship(
                TemplateId, PhysicalId, "template-pool-member", facts.InventoryCapturedAt))
        };
    }
}
