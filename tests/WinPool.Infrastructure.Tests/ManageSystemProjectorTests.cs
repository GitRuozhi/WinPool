using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class ManageSystemProjectorTests
{
    [Fact]
    public void ProjectionPreservesIdentityHierarchyAndOccurrenceKeys()
    {
        var document = Document();

        var projection = new ManageSystemProjector().Project(document);
        var nodes = Flatten(projection.Root).ToArray();

        Assert.Equal(document.Id, projection.DocumentId);
        Assert.Equal(StorageSystemSourceKind.Simulation, projection.SourceKind);
        Assert.Equal(InternalStableIdentity.SystemFromDocumentId(document.Id), projection.SystemId);
        Assert.Equal(document.Id, projection.Root.Id.ProviderKey);
        Assert.All(nodes, node => Assert.Equal(projection.SystemId, node.Id.System));
        Assert.Equal(nodes.Length, nodes.Select(node => node.OccurrenceKey).Distinct().Count());
        Assert.Contains(nodes, node => node.Role == ManageObjectRole.StoragePool);
        Assert.Contains(nodes, node => node.Role == ManageObjectRole.StorageTier);
        Assert.Contains(nodes, node => node.Role == ManageObjectRole.VirtualDisk);
        Assert.Contains(nodes, node => node.Role == ManageObjectRole.Partition);
        Assert.StartsWith($"{document.Id}:root", projection.Root.OccurrenceKey);
        Assert.Equal(
            [ManageObjectRole.System, ManageObjectRole.StoragePool,
             ManageObjectRole.StorageTier, ManageObjectRole.PhysicalDisk,
             ManageObjectRole.VirtualDisk, ManageObjectRole.Partition,
             ManageObjectRole.Volume],
            projection.WorkspaceObjects.Select(item => item.Role));
    }

    [Fact]
    public void LogicalGroupsKeepDistinctPresentationRolesWithoutBecomingPools()
    {
        var source = Document();
        var otherDisk = new OsDiskInfo(
            "osdisk:other", "Other", 9, "GPT", 8_000_000,
            false, false, false, null, null);
        var network = new NetworkDiskInfo(
            "network:r", true, "R: Network", "R", "\\\\server\\share",
            "NTFS", 4_000_000, 1_000_000);
        var document = source with
        {
            Snapshot = source.Snapshot with
            {
                NetworkDisks = [network],
                OsDisks = source.Snapshot.OsDisks.Append(otherDisk).ToArray()
            }
        };

        var nodes = Flatten(new ManageSystemProjector().Project(document).Root)
            .ToArray();
        var networkGroup = Assert.Single(
            nodes,
            node => node.Role == ManageObjectRole.NetworkGroup);
        var otherGroup = Assert.Single(
            nodes,
            node => node.Role == ManageObjectRole.OtherGroup);

        Assert.Equal(WinPool.Domain.StorageObjectKind.LogicalGroup, networkGroup.Id.Kind);
        Assert.Equal(WinPool.Domain.StorageObjectKind.LogicalGroup, otherGroup.Id.Kind);
        Assert.DoesNotContain(
            nodes.Where(node => node.Role == ManageObjectRole.StoragePool),
            node => node.Id.ProviderKey == networkGroup.Id.ProviderKey
                || node.Id.ProviderKey == otherGroup.Id.ProviderKey);
    }

    [Fact]
    public void ComparisonProjectionPreservesPropertyOrderAndPresentationHints()
    {
        var document = Document();
        var system = InternalStableIdentity.SystemFromDocumentId(document.Id);
        var projector = new ManageComparisonProjector();

        var systemView = projector.Project(
            document,
            new WinPool.Domain.StorageObjectId(
                system,
                WinPool.Domain.StorageObjectKind.System,
                document.Id),
            ManageObjectRole.System);
        Assert.Equal(
            ["HostName", "Version", "VersionNumber", "OsBuild", "Cpu", "Memory",
             "LocalStorage", "StoragePool", "PhysicalDisk", "VirtualDisk",
             "Partition", "AccessibleVolumes"],
            systemView.Properties.Select(property => property.PropertyTextKey));
        Assert.Equal(
            ManageValuePresentation.ProductName,
            systemView.Properties.Single(property => property.PropertyTextKey == "Version").Presentation);

        var poolView = projector.Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.StoragePool, "pool:1"),
            ManageObjectRole.StoragePool);
        Assert.Equal(
            ["Type", "Capacity", "PhysicalDisk", "VirtualDisk", "RunningStatus",
             "Health", "ProvisioningType", "Resiliency", "PhysicalSector",
             "LogicalSector", "PerformanceTier", "CapacityTier"],
            poolView.Properties.Select(property => property.PropertyTextKey));
        Assert.Equal(
            ManageValuePresentation.LocalizationKey,
            poolView.Properties[0].Presentation);

        var tierView = projector.Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.StorageTier, "tier:1"),
            ManageObjectRole.StorageTier);
        Assert.Equal(
            ["PoolOwner", "Media", "Type", "Capacity", "ProvisioningType",
             "Resiliency", "FaultTolerance", "PhysicalDisk", "Columns",
             "Interleave", "AllocationUnit"],
            tierView.Properties.Select(property => property.PropertyTextKey));

        var physicalView = projector.Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.PhysicalDisk, "physical:1"),
            ManageObjectRole.PhysicalDisk);
        var serial = physicalView.Properties.Single(property => property.PropertyTextKey == "Serial");
        Assert.Equal("masked", serial.RawValue);
        Assert.Equal(ManageValuePresentation.MaskedSerial, serial.Presentation);

        var partitionView = projector.Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.Partition, "partition:1"),
            ManageObjectRole.Partition);
        Assert.Equal(
            ["OwningDisk", "Type", "FileSystem", "AllocationUnit", "Capacity",
             "Available", "SystemPartition", "PartitionStatus", "StartOffset",
             "DriveLetter", "VolumeLabel", "Path"],
            partitionView.Properties.Select(property => property.PropertyTextKey));
        Assert.Equal(
            ManageValuePresentation.PartitionType,
            partitionView.Properties.Single(property => property.PropertyTextKey == "Type").Presentation);
        var allocationUnit = partitionView.Properties.Single(
            property => property.PropertyTextKey == "AllocationUnit").RawValue;
        Assert.NotEmpty(allocationUnit);
        var topologyPartition = Flatten(new ManageSystemProjector().Project(document).Root)
            .First(node => node.Role == ManageObjectRole.Partition);
        Assert.DoesNotContain(allocationUnit, topologyPartition.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonProjectionIgnoresCrossSystemObjectIdentity()
    {
        var document = Document();
        var foreign = InternalStableIdentity.SystemFromDocumentId("simulation:foreign");

        var view = new ManageComparisonProjector().Project(
            document,
            Object(foreign, WinPool.Domain.StorageObjectKind.StoragePool, "pool:1"),
            ManageObjectRole.StoragePool);

        Assert.Empty(view.Properties);
    }

    [Fact]
    public void DetailsProjectionPreservesFrozenRowsAndDefersDisplayPolicies()
    {
        var document = Document();
        var system = InternalStableIdentity.SystemFromDocumentId(document.Id);
        var projector = new ManageDetailsProjector();

        var physical = projector.Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.PhysicalDisk, "physical:1"),
            ManageObjectRole.PhysicalDisk,
            "Disk One");
        Assert.Equal(
            ["Model", "Serial", "Bus", "Media", "Capacity", "Health", "CanPool", "CannotPoolReason", "LastScan"],
            physical.Properties.Select(property => property.PropertyTextKey));
        Assert.Equal(
            ManageValuePresentation.MaskedSerial,
            physical.Properties.Single(property => property.PropertyTextKey == "Serial").Presentation);
        Assert.Equal(
            ManageValuePresentation.LocalizationKey,
            physical.Properties.Single(property => property.PropertyTextKey == "CanPool").Presentation);
        Assert.Equal(
            ManageValuePresentation.LocalDateTime,
            physical.Properties[^1].Presentation);

        var partition = projector.Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.Partition, "partition:1"),
            ManageObjectRole.Partition,
            "fallback");
        Assert.Equal("C: Data", partition.DisplayName);
        Assert.Equal(
            ["Type", "FileSystem", "AllocationUnit", "Capacity", "Available", "Health", "Path", "LastScan"],
            partition.Properties.Select(property => property.PropertyTextKey));
        Assert.Equal(
            ManageValuePresentation.PartitionType,
            partition.Properties[0].Presentation);
    }

    [Fact]
    public void NavigationProjectionMapsPartitionAcrossAllRelatedCategories()
    {
        var document = Document();
        var system = InternalStableIdentity.SystemFromDocumentId(document.Id);
        var view = new ManageNavigationProjector().Project(
            document,
            Object(system, WinPool.Domain.StorageObjectKind.Partition, "partition:1"),
            ManageObjectRole.Partition);

        Assert.Equal(
            document.Id,
            view.RelatedSelections[ManageWorkspaceCategory.System]!.Id.ProviderKey);
        Assert.Equal(
            "pool:1",
            view.RelatedSelections[ManageWorkspaceCategory.Pool]!.Id.ProviderKey);
        Assert.Equal(
            "tier:1",
            view.RelatedSelections[ManageWorkspaceCategory.Tier]!.Id.ProviderKey);
        Assert.Equal(
            "virtual:1",
            view.RelatedSelections[ManageWorkspaceCategory.Disk]!.Id.ProviderKey);
        Assert.Equal(
            "partition:1",
            view.RelatedSelections[ManageWorkspaceCategory.Partition]!.Id.ProviderKey);
        Assert.Equal(ManageObjectRole.VirtualDisk, view.PrimaryTarget!.Role);
        Assert.Equal("virtual:1", view.PrimaryTarget.Id.ProviderKey);
    }

    [Fact]
    public void CommandProjectionKeepsLocalMutationDisabledAndResolvesDialogTargets()
    {
        var simulation = Document();
        var local = simulation with
        {
            Id = "local:manage-test",
            SystemId = InternalStableIdentity.SystemFromDocumentId("local:manage-test"),
            Kind = StorageSystemKind.Local,
            DisplayName = "Local"
        };
        var system = InternalStableIdentity.SystemFromDocumentId(simulation.Id);
        var projector = new ManageCommandProjector();
        var partition = projector.Project(
            simulation,
            local,
            Object(system, WinPool.Domain.StorageObjectKind.Partition, "partition:1"),
            ManageObjectRole.Partition,
            ManageWorkspaceCategory.Partition);

        Assert.True(partition.Commands.Single(command =>
            command.Kind == ManageCommandKind.EditPartition).IsEnabled);
        Assert.False(partition.Commands.Single(command =>
            command.Kind == ManageCommandKind.OpenExplorer).IsEnabled);
        Assert.False(partition.Commands.Single(command =>
            command.Kind == ManageCommandKind.ShowSystemProperties).IsEnabled);
        Assert.True(partition.Commands.Single(command =>
            command.Kind == ManageCommandKind.ExportCategory).IsEnabled);
        Assert.True(partition.SystemDialogTarget.HasResolvedPartition);
        Assert.Equal("C:\\", partition.SystemDialogTarget.PartitionPath);
        Assert.Equal("C", partition.SystemDialogTarget.DriveLetter);

        var localSystem = InternalStableIdentity.SystemFromDocumentId(local.Id);
        var localPool = projector.Project(
            local,
            local,
            Object(localSystem, WinPool.Domain.StorageObjectKind.StoragePool, "pool:1"),
            ManageObjectRole.StoragePool,
            ManageWorkspaceCategory.Pool);
        Assert.All(
            localPool.Commands.Where(command => command.Kind != ManageCommandKind.ExportCategory),
            command => Assert.False(command.IsEnabled));
    }

    [Fact]
    public void VolumeWorkspaceObjectsIncludeOnlyDriveLetterPartitions()
    {
        var source = Document();
        var letterless = new PartitionInfo(
            "partition:2", true, 3, 2, "WindowsRecovery", 2_000_000, 100_000,
            false, false, "", "Recovery", "", null, 0,
            "Healthy", "OK", "", "osdisk:3");
        var document = source with
        {
            Snapshot = source.Snapshot with
            {
                Partitions = source.Snapshot.Partitions.Append(letterless).ToArray()
            }
        };

        var volumes = new ManageSystemProjector().Project(document).WorkspaceObjects
            .Where(item => item.Role == ManageObjectRole.Volume)
            .ToList();

        var volume = Assert.Single(volumes);
        Assert.Equal(ManageWorkspaceCategory.Volume, volume.Category);
        Assert.Equal("partition:1", volume.Id.ProviderKey);
        Assert.Equal("C: Data", volume.DisplayName);
        Assert.Equal(WinPool.Domain.StorageObjectKind.Partition, volume.Id.Kind);
    }

    [Fact]
    public void VolumeProjectionMatchesPartitionAcrossComparisonDetailsAndNavigation()
    {
        var document = Document();
        var system = InternalStableIdentity.SystemFromDocumentId(document.Id);
        var id = Object(system, WinPool.Domain.StorageObjectKind.Partition, "partition:1");

        var comparison = new ManageComparisonProjector().Project(document, id, ManageObjectRole.Volume);
        Assert.Equal(
            ["OwningDisk", "Type", "FileSystem", "AllocationUnit", "Capacity",
             "Available", "SystemPartition", "PartitionStatus", "StartOffset",
             "DriveLetter", "VolumeLabel", "Path"],
            comparison.Properties.Select(property => property.PropertyTextKey));

        var details = new ManageDetailsProjector().Project(document, id, ManageObjectRole.Volume, "fallback");
        Assert.Equal("C: Data", details.DisplayName);
        Assert.Equal(
            ["Type", "FileSystem", "AllocationUnit", "Capacity", "Available", "Health", "Path", "LastScan"],
            details.Properties.Select(property => property.PropertyTextKey));

        var navigation = new ManageNavigationProjector().Project(document, id, ManageObjectRole.Volume);
        Assert.Equal(
            "partition:1",
            navigation.RelatedSelections[ManageWorkspaceCategory.Partition]!.Id.ProviderKey);
        Assert.Equal(
            "partition:1",
            navigation.RelatedSelections[ManageWorkspaceCategory.Volume]!.Id.ProviderKey);

        var local = document with
        {
            Id = "local:volume-test",
            SystemId = InternalStableIdentity.SystemFromDocumentId("local:volume-test"),
            Kind = StorageSystemKind.Local,
            DisplayName = "Local"
        };
        var commands = new ManageCommandProjector().Project(
            document,
            local,
            id,
            ManageObjectRole.Volume,
            ManageWorkspaceCategory.Volume);
        Assert.True(commands.Commands.Single(command =>
            command.Kind == ManageCommandKind.EditPartition).IsEnabled);
        Assert.True(commands.SystemDialogTarget.HasResolvedPartition);
    }

    [Fact]
    public void PoolMemberGroupsKeepNamedLayersInsteadOfHeadlessNodes()
    {
        var source = Document();
        var extra = new PhysicalDiskInfo(
            "physical:2", true, "Spare One", "Model", "masked", "SATA", "HDD",
            2_000_000, 512, 4096, "Healthy", "OK", false, "In a pool", 2,
            false, false, false, false, "pool:1");
        var secondVirtual = source.Snapshot.VirtualDisks[0] with
        {
            StableId = "virtual:2",
            FriendlyName = "Virtual02",
            TierStableIds = [],
            OsDiskNumbers = [4]
        };
        var document = source with
        {
            Snapshot = source.Snapshot with
            {
                PhysicalDisks = source.Snapshot.PhysicalDisks.Append(extra).ToArray(),
                StoragePools = [source.Snapshot.StoragePools[0] with
                {
                    MemberPhysicalDiskIds = ["physical:1", "physical:2"]
                }],
                VirtualDisks = source.Snapshot.VirtualDisks.Append(secondVirtual).ToArray()
            }
        };

        var nodes = Flatten(new ManageSystemProjector().Project(document).Root).ToArray();
        var directGroup = Assert.Single(
            nodes,
            node => node.Role == ManageObjectRole.DirectDiskGroup);
        var virtualGroup = Assert.Single(
            nodes,
            node => node.Role == ManageObjectRole.VirtualDiskGroup);

        Assert.Equal("Unallocated", directGroup.DisplayName);
        Assert.Contains("physical disks", directGroup.Summary, StringComparison.Ordinal);
        Assert.Equal("Virtual disks", virtualGroup.DisplayName);
        Assert.Contains("virtual disks", virtualGroup.Summary, StringComparison.Ordinal);

        // The unallocated members are surfaced as a tier-category object.
        var projection = new ManageSystemProjector().Project(document);
        var unallocatedItem = Assert.Single(
            projection.WorkspaceObjects,
            item => item.Role == ManageObjectRole.DirectDiskGroup);
        Assert.Equal(ManageWorkspaceCategory.Tier, unallocatedItem.Category);
        Assert.Equal("group:direct:pool:1", unallocatedItem.Id.ProviderKey);
    }

    [Fact]
    public void NetworkDisksWithDriveLettersAppearInTheVolumeCategory()
    {
        var source = Document();
        var lettered = new NetworkDiskInfo(
            "network:r", true, "R: Network", "R", "\\\\server\\share",
            "NTFS", 4_000_000, 1_000_000);
        var letterless = new NetworkDiskInfo(
            "network:s", true, "S: Share", "S", "\\\\server\\share2",
            "NTFS", 4_000_000, 1_000_000);
        var document = source with
        {
            Snapshot = source.Snapshot with
            {
                NetworkDisks = [lettered, letterless with { DriveLetter = "" }]
            }
        };

        var volumes = new ManageSystemProjector().Project(document).WorkspaceObjects
            .Where(item => item.Category == ManageWorkspaceCategory.Volume)
            .ToList();

        Assert.Equal(ManageObjectRole.Volume, volumes[0].Role);
        Assert.Equal("partition:1", volumes[0].Id.ProviderKey);
        var networkVolume = Assert.Single(
            volumes,
            item => item.Role == ManageObjectRole.NetworkDisk);
        Assert.Equal("network:r", networkVolume.Id.ProviderKey);
        Assert.DoesNotContain(volumes, item => item.Id.ProviderKey == "network:s");
    }

    [Fact]
    public void UnallocatedGroupProjectsLikeATierAcrossDetailsAndComparison()
    {
        var source = Document();
        var extra = new PhysicalDiskInfo(
            "physical:2", true, "Spare One", "Model", "masked", "SATA", "HDD",
            2_000_000, 512, 4096, "Healthy", "OK", false, "In a pool", 2,
            false, false, false, false, "pool:1");
        var document = source with
        {
            Snapshot = source.Snapshot with
            {
                PhysicalDisks = source.Snapshot.PhysicalDisks.Append(extra).ToArray(),
                StoragePools = [source.Snapshot.StoragePools[0] with
                {
                    MemberPhysicalDiskIds = ["physical:1", "physical:2"]
                }],
                StorageTiers = [source.Snapshot.StorageTiers[0] with
                {
                    MemberPhysicalDiskIds = ["physical:1"]
                }]
            }
        };
        var system = InternalStableIdentity.SystemFromDocumentId(document.Id);
        var id = Object(system, WinPool.Domain.StorageObjectKind.LogicalGroup, "group:direct:pool:1");

        var comparison = new ManageComparisonProjector().Project(
            document, id, ManageObjectRole.DirectDiskGroup);
        Assert.Contains(
            comparison.Properties,
            property => property.PropertyTextKey == "Type" && property.RawValue == "UnallocatedLayer");
        Assert.Contains(
            comparison.Properties,
            property => property.PropertyTextKey == "PhysicalDisk" && property.RawValue == "1");
        Assert.Contains(
            comparison.Properties,
            property => property.PropertyTextKey == "Capacity"
                && property.RawValue == TopologyProjector.FormatBytes(2_000_000));

        var details = new ManageDetailsProjector().Project(
            document, id, ManageObjectRole.DirectDiskGroup, "Unallocated");
        Assert.Equal("Unallocated", details.DisplayName);
        Assert.Contains(
            details.Properties,
            property => property.PropertyTextKey == "Members" && property.RawValue == "1");

        var navigation = new ManageNavigationProjector().Project(
            document, id, ManageObjectRole.DirectDiskGroup);
        Assert.Equal(
            "pool:1",
            navigation.RelatedSelections[ManageWorkspaceCategory.Pool]!.Id.ProviderKey);
        Assert.Equal(
            "group:direct:pool:1",
            navigation.RelatedSelections[ManageWorkspaceCategory.Tier]!.Id.ProviderKey);
    }

    private static WinPool.Domain.StorageObjectId Object(
        WinPool.Domain.SystemId system,
        WinPool.Domain.StorageObjectKind kind,
        string providerKey) =>
        new(system, kind, providerKey);

    private static IEnumerable<ManageTopologyNodeView> Flatten(
        ManageTopologyNodeView node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    private static StorageSystemDocument Document()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var physical = new PhysicalDiskInfo(
            "physical:1", true, "Disk One", "Model", "masked", "SATA", "HDD",
            2_000_000, 512, 4096, "Healthy", "OK", false, "In a pool", 1,
            false, false, false, false, "pool:1");
        var pool = new StoragePoolInfo(
            "pool:1", true, "Pool01", false, "Healthy", "OK",
            2_000_000, 1_000_000, "subsystem:1", ["physical:1"]);
        var tier = new StorageTierInfo(
            "tier:1", true, "Capacity", "HDD", "Parity", 1_000_000, 1_000_000,
            "pool:1", "virtual:1", ["physical:1"]);
        var virtualDisk = new VirtualDiskInfo(
            "virtual:1", true, "Virtual01", "Healthy", "OK", "Parity", "Fixed",
            3, 65536, 1_000_000, 1_000_000, "pool:1", ["tier:1"], [3]);
        var osDisk = new OsDiskInfo(
            "osdisk:3", "Virtual01", 3, "GPT", 1_000_000,
            false, false, false, null, "virtual:1");
        var partition = new PartitionInfo(
            "partition:1", true, 3, 1, "Primary", 1_048_576, 900_000,
            false, false, "C", "Data", "NTFS", 4096, 400_000,
            "Healthy", "OK", "C:\\", "osdisk:3");
        var snapshot = new StorageSnapshot(
            2, "test", now,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", now),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [physical], [pool], [tier], [virtualDisk], [osDisk], [partition], [],
            [new StorageRelationship("pool:1", "physical:1", "PoolMember")],
            []);
        return new(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:manage-test",
            StorageSystemKind.Simulation,
            "Test",
            snapshot,
            HardwareInventoryReport.Empty(now),
            [],
            now);
    }
}
