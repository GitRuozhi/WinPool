using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>
/// App-side proposals contain requested values only. The Agent rereads Windows
/// facts and replaces every condition, impact statement and support assertion.
/// </summary>
public static class RealOperationProposalFactory
{
    public sealed record VirtualDiskOptions(
        string Name, long SizeBytes, bool InitializeAndPartition,
        bool CreateMsr, bool FormatNtfs, string? Label, char? Letter, bool UseMaximumSize = false,
        StorageLayoutParameters? Layout = null);

    public static RealOperationIntentRequest OneStep(
        SystemId systemId, OperationIntent intent, StorageObjectId target,
        RealStorageCommand command, string expectedState, string dataLoss)
    {
        var request = new RealOperationIntentRequest(intent, systemId,
            [target], [new RealOperationStep("operation-1", command, [],
                "Agent live target verification required", expectedState,
                dataLoss, "Agent live Windows preflight required")], expectedState);
        return request;
    }

    public static RealOperationIntentRequest DeclareCurrentPropertyPoolMembers(
        RealOperationIntentRequest request, StorageSnapshot snapshot) => DeclareCurrentPoolMembers(request, snapshot);

    public static RealOperationIntentRequest DeclareCurrentPoolMembers(
        RealOperationIntentRequest request, StorageSnapshot snapshot)
    {
        if (!OperationSecurityCatalog.IsStorageStructureMutation(request.Intent)) return request;
        string? DiskPool(string? diskId)
        {
            var disk = snapshot.OsDisks.SingleOrDefault(item => item.StableId == diskId);
            return snapshot.VirtualDisks.SingleOrDefault(item => item.StableId == disk?.VirtualDiskStableId)?.PoolStableId;
        }
        string? PartitionPool(string? partitionId) => DiskPool(snapshot.Partitions.SingleOrDefault(item => item.StableId == partitionId)?.OsDiskStableId);
        string? TargetPool(StorageObjectId target) => target.Kind switch
        {
            StorageObjectKind.StoragePool => target.ProviderKey,
            StorageObjectKind.VirtualDisk => snapshot.VirtualDisks.Single(item => item.StableId == target.ProviderKey).PoolStableId,
            StorageObjectKind.StorageTier => snapshot.StorageTiers.Single(item => item.StableId == target.ProviderKey).PoolStableId,
            StorageObjectKind.Volume => PartitionPool(snapshot.Volumes.Single(item => item.StableId == target.ProviderKey).PartitionStableId),
            StorageObjectKind.Partition => PartitionPool(target.ProviderKey),
            StorageObjectKind.OsDisk => DiskPool(target.ProviderKey),
            _ => null
        };
        var poolIds = request.Targets.Where(item => item.Kind != StorageObjectKind.PhysicalDisk)
            .Select(TargetPool).Where(id => id is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (poolIds.Length == 0) return request;
        if (poolIds.Length != 1) throw new InvalidDataException("One typed storage operation cannot span concrete pools.");
        var poolId = poolIds[0]!;
        var pool = snapshot.StoragePools.Single(item => item.StableId == poolId);
        if (pool.IsPrimordial || !pool.IsStable || pool.MemberPhysicalDiskIds.Count == 0
            || pool.MemberPhysicalDiskIds.Distinct(StringComparer.Ordinal).Count() != pool.MemberPhysicalDiskIds.Count)
            throw new InvalidDataException("The storage target lacks a complete current concrete pool member set.");
        var members = pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(request.SystemId, StorageObjectKind.PhysicalDisk, id)).ToArray();
        var declared = request.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (declared.Length > 0 && !declared.ToHashSet().SetEquals(members))
            throw new InvalidDataException("The declared storage member set differs from its current pool.");
        var related = request.Targets.Any(item => item.Kind == StorageObjectKind.StorageTier)
            ? new[] { new StorageObjectId(request.SystemId, StorageObjectKind.StoragePool, poolId) } : [];
        var result = request with { Targets = request.Targets.Concat(related).Concat(members).Distinct().ToArray() };
        RealOperationValidator.Validate(result);
        return result;
    }

    public static RealOperationIntentRequest ClearToRaw(
        SystemId systemId, StorageObjectId osDisk)
    {
        var request = OneStep(systemId, OperationIntent.ClearDisk, osDisk,
            new ClearDiskCommand(RealTargetReference.ForExisting(osDisk), false),
            RealOperationValidator.ClearDiskExpectedFinalState,
            "All partitions, volumes, files and drive letters on the exact disk are lost");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest InitializeGpt(
        SystemId systemId, StorageObjectId osDisk)
    {
        var target = RealTargetReference.ForExisting(osDisk);
        var steps = new List<RealOperationStep>
        {
            new("initialize-gpt", new InitializeGptCommand(target), [],
                "The exact OS disk is RAW and has zero partitions",
                "The exact disk is GPT; any provider-created MSR will be read before a separate layout plan",
                "GPT metadata replaces any prior partition table",
                "Agent live Windows preflight required")
        };
        var request = new RealOperationIntentRequest(OperationIntent.InitializeDisk,
            systemId, [osDisk], steps,
            "GPT disk initialized; any provider-created MSR is reconciled after a fresh read in a separate plan");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest? ConfigureInitializedDisk(
        SystemId systemId, StorageObjectId osDisk, StorageObjectId? existingMsr,
        bool createMsr, long? virtualDiskSizeBytes = null,
        bool formatNtfs = false, string? label = null, char? letter = null,
        int allocationUnitBytes = 65536, string formatFileSystem = "NTFS")
    {
        const long mib = 1024L * 1024;
        const long msrOffset = mib;
        const long msrSize = 16 * mib;
        (long OffsetBytes, long SizeBytes)? dataGeometry = virtualDiskSizeBytes is long requestedSize
            ? GetAutomaticDataGeometry(requestedSize, createMsr) : null;
        var realFileSystem = formatNtfs ? formatFileSystem.Trim().ToUpperInvariant() switch
        {
            "NTFS" => RealFileSystem.Ntfs,
            "EXFAT" => RealFileSystem.ExFat,
            "REFS" => RealFileSystem.ReFs,
            _ => (RealFileSystem?)null
        } : null;
        if ((formatNtfs && virtualDiskSizeBytes is null) ||
            ((formatNtfs || letter is not null) && virtualDiskSizeBytes is null) ||
            (letter is { } driveLetter && (driveLetter < 'D' || driveLetter > 'Z')) ||
            (formatNtfs && (realFileSystem is null
                || !FormatAllocationUnitPolicy.IsKnownClusterSize(realFileSystem.Value.ToString(), allocationUnitBytes))))
        {
            throw new ArgumentException("The initialized-disk layout needs a positive whole-MiB size and a valid local drive letter.");
        }
        if (existingMsr is { } msrTarget &&
            (msrTarget.System != systemId || msrTarget.Kind != StorageObjectKind.Partition))
        {
            throw new ArgumentException("The existing MSR must be an exact partition target in the requested system.", nameof(existingMsr));
        }
        if (existingMsr is null && !createMsr && virtualDiskSizeBytes is null)
            return null;

        var targets = new List<StorageObjectId> { osDisk };
        if (existingMsr is { } existing)
            targets.Add(existing);
        var steps = new List<RealOperationStep>();
        string? previous = null;
        if (existingMsr is { } oldMsr)
        {
            steps.Add(new RealOperationStep("delete-auto-msr",
                new DeletePartitionCommand(RealTargetReference.ForExisting(oldMsr)), [],
                "Fresh inventory identifies this exact MSR GPT partition on the initialized disk",
                "The exact provider-created MSR is absent",
                "The selected reserved partition metadata is removed",
                "Agent live Windows identity and role verification required"));
            previous = "delete-auto-msr";
        }

        if (createMsr)
        {
            steps.Add(new RealOperationStep("create-msr",
                new CreatePartitionCommand(RealTargetReference.ForExisting(osDisk),
                    RealPartitionRole.Msr, msrOffset, msrSize),
                previous is null ? [] : [previous],
                "The exact GPT disk has a free 16 MiB range at 1 MiB",
                "A 16 MiB Microsoft Reserved partition exists at 1 MiB",
                "The reserved range is no longer available for data",
                "Agent live Windows geometry check required"));
            previous = "create-msr";
        }

        if (dataGeometry is { } data)
        {
            var (dataOffset, dataSize) = data;
            steps.Add(new RealOperationStep("create-data",
                new CreatePartitionCommand(RealTargetReference.ForExisting(osDisk),
                    RealPartitionRole.BasicData, dataOffset, dataSize),
                previous is null ? [] : [previous],
                "The exact GPT disk has the requested free BasicData range",
                $"A BasicData partition exists at {dataOffset} bytes with size {dataSize} bytes",
                "Data in the selected range becomes inaccessible",
                "Agent live Windows geometry check required"));
            previous = "create-data";
            var partition = RealTargetReference.FromStep(StorageObjectKind.Partition, previous);
            if (formatNtfs)
            {
                steps.Add(new RealOperationStep("format-data",
                    new FormatVolumeCommand(partition, realFileSystem!.Value,
                        allocationUnitBytes, false, label), [previous],
                    "The new BasicData partition exists",
                    $"The partition is quickly formatted {realFileSystem} with {FormatClusterSize(allocationUnitBytes)} clusters",
                    "Formatting erases all data in the new partition range",
                    $"Agent live {realFileSystem} file-system and allocation-unit support required"));
                previous = "format-data";
            }
            if (letter is { } requestedLetter)
            {
                steps.Add(new RealOperationStep("assign-data-letter",
                    new SetDriveLetterCommand(partition, null, requestedLetter),
                    previous == "create-data" ? ["create-data"] : ["create-data", previous],
                    "The new partition has no drive letter",
                    "The requested drive letter is assigned",
                    "No additional data loss expected",
                    "Agent live drive-letter availability check required"));
            }
        }

        var expected = virtualDiskSizeBytes is null
            ? createMsr ? "Initialized GPT disk with one exact 16 MiB MSR at 1 MiB" : "Initialized GPT disk without an MSR"
            : $"Initialized GPT disk with {(createMsr ? "an MSR and " : string.Empty)}BasicData partition; optional {realFileSystem?.ToString() ?? "no"} format and drive letter are explicit";
        var request = new RealOperationIntentRequest(OperationIntent.InitializeDisk,
            systemId, targets, steps, expected);
        RealOperationValidator.Validate(request);
        return request;
    }

    /// <summary>Validates explicit bytes only. MAX inputs are frozen by the Agent from fresh facts.</summary>
    public static long ResolveVirtualDiskCreationSize(
        RealVirtualDiskCreationRange range, bool useMaximum, long? requestedSizeBytes,
        bool autoCreatePartition, bool createMsr)
    {
        if (useMaximum)
            throw new InvalidOperationException("MAX must use the typed intent; the Agent freezes and executes the capacity search.");
        var bytes = requestedSizeBytes ?? throw new InvalidDataException("The exact requested capacity is missing.");
        if (!range.Supports(bytes))
            throw new InvalidDataException("The requested capacity is outside the exact provider creation range.");
        if (autoCreatePartition) ValidateAutomaticLayoutCapacity(bytes, createMsr);
        return bytes;
    }

    public static void ValidateAutomaticLayoutCapacity(long sizeBytes, bool createMsr) =>
        _ = GetAutomaticDataGeometry(sizeBytes, createMsr);

    private static (long OffsetBytes, long SizeBytes) GetAutomaticDataGeometry(long sizeBytes, bool createMsr)
    {
        var geometry = EditWorkspace.GetAutomaticPartitionCreateGeometry(sizeBytes, createMsr);
        return (geometry.StartOffsetBytes!.Value, geometry.MaximumSizeBytes!.Value);
    }

    private static string FormatClusterSize(int bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes % (1024 * 1024) == 0
            ? $"{bytes / (1024 * 1024)} MiB"
            : $"{bytes / 1024} KiB";

    public static RealOperationIntentRequest? TryFormatExistingData(
        SystemId systemId, StorageObjectId partition, string? fileSystem,
        int clusterBytes, bool fullFormat, string? label)
    {
        var format = fileSystem?.ToUpperInvariant() switch
        {
            "NTFS" when FormatAllocationUnitPolicy.IsKnownClusterSize("NTFS", clusterBytes) => RealFileSystem.Ntfs,
            "EXFAT" when FormatAllocationUnitPolicy.IsKnownClusterSize("exFAT", clusterBytes) => RealFileSystem.ExFat,
            "REFS" when FormatAllocationUnitPolicy.IsKnownClusterSize("ReFS", clusterBytes) && !fullFormat => RealFileSystem.ReFs,
            _ => (RealFileSystem?)null
        };
        if (format is null)
            return null;
        var request = OneStep(systemId, OperationIntent.FormatVolume, partition,
            new FormatVolumeCommand(RealTargetReference.ForExisting(partition),
                format.Value, clusterBytes, fullFormat, label),
            $"Partition formatted as {format.Value}",
            format == RealFileSystem.ReFs
                ? "All existing files and volume data on the selected partition are erased; ReFS has no long-run evidence equivalent to the current tested 64 KiB NTFS recommendation"
                : "All existing files and volume data on the selected partition are erased");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreatePartition(
        SystemId systemId, StorageObjectId disk,
        RealPartitionRole role, long offsetBytes, long sizeBytes,
        RealFileSystem? fileSystem, int clusterBytes, bool fullFormat,
        string? label, char? letter)
    {
        if (fileSystem is { } selectedFileSystem)
        {
            var supported = role switch
            {
                RealPartitionRole.BasicData => selectedFileSystem is RealFileSystem.Ntfs or RealFileSystem.ExFat or RealFileSystem.ReFs
                    && FormatAllocationUnitPolicy.IsKnownClusterSize(selectedFileSystem.ToString(), clusterBytes)
                    && (selectedFileSystem != RealFileSystem.ReFs || !fullFormat),
                RealPartitionRole.Efi => selectedFileSystem == RealFileSystem.Fat32 && clusterBytes == 4096 && !fullFormat,
                RealPartitionRole.Recovery => selectedFileSystem == RealFileSystem.Ntfs && clusterBytes == 4096 && !fullFormat,
                _ => false
            };
            if (!supported)
                throw new ArgumentException("The file system and allocation unit do not match the selected partition role or supported range.", nameof(fileSystem));
        }
        else if (role is RealPartitionRole.Efi or RealPartitionRole.Recovery)
        {
            throw new ArgumentException("A newly created EFI or Recovery partition requires its fixed filesystem format.", nameof(fileSystem));
        }
        var steps = new List<RealOperationStep>
        {
            new("create-partition",
                new CreatePartitionCommand(RealTargetReference.ForExisting(disk),
                    role, offsetBytes, sizeBytes), [],
                "Agent live unallocated-range verification required",
                "The requested partition exists",
                "Data in the selected range becomes inaccessible",
                "Agent live Windows preflight required")
        };
        if (fileSystem is { } format)
        {
            steps.Add(new RealOperationStep("format-volume",
                new FormatVolumeCommand(
                    RealTargetReference.FromStep(StorageObjectKind.Partition,
                        "create-partition"), format, clusterBytes, fullFormat, label),
                ["create-partition"],
                "The new partition has been verified",
                "The new partition is formatted",
                "All existing data in the partition range is erased",
                "Agent live Windows format support required"));
        }
        if (letter is { } driveLetter)
        {
            var lastStep = steps[^1].Id;
            steps.Add(new RealOperationStep("assign-letter",
                new SetDriveLetterCommand(
                    RealTargetReference.FromStep(StorageObjectKind.Partition,
                        "create-partition"), null, driveLetter),
                lastStep == "create-partition" ? ["create-partition"]
                    : ["create-partition", lastStep],
                "The new partition has no drive letter",
                "The selected drive letter is assigned",
                "No additional partition data loss expected",
                "Agent live Windows drive-letter check required"));
        }

        var expected = $"GPT partition at {offsetBytes} bytes with size {sizeBytes} bytes";
        var request = new RealOperationIntentRequest(OperationIntent.CreatePartition,
            systemId, [disk], steps, expected);
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateFirstVirtualDisk(
        SystemId systemId, StorageObjectId pool, VirtualDiskOptions options)
    {
        var steps = new List<RealOperationStep>();
        AppendVirtualDiskSteps(steps, RealTargetReference.ForExisting(pool), options);
        var request = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk,
            systemId, [pool], steps,
            $"{(options.Layout ?? StorageLayoutParameters.LegacyDefault).ResiliencySettingName} Fixed virtual disk {options.Name} ({CreationCapacity(options.SizeBytes, options.UseMaximumSize)})" +
            (options.InitializeAndPartition
                ? "; GPT metadata only, with provider-created MSR and data layout handled after a fresh read"
                : "; virtual disk remains RAW without partitions"));
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateFirstVirtualDisk(
        SystemId systemId, StorageObjectId pool, IReadOnlyList<StorageObjectId> members, VirtualDiskOptions options)
    {
        var request = CreateFirstVirtualDisk(systemId, pool, options) with
        {
            Targets = PoolTargets(systemId, pool, members)
        };
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreatePoolWithMembers(
        SystemId systemId, IReadOnlyList<StorageObjectId> members, string name)
    {
        ValidateMembers(systemId, members);
        var references = members.Select(RealTargetReference.ForExisting).ToArray();
        var request = new RealOperationIntentRequest(OperationIntent.CreateStoragePool, systemId, members.ToArray(),
            [new RealOperationStep("create-pool", new CreatePoolCommand(references[0], name, references), [],
                "Every exact declared physical member is freshly verified RAW, unpartitioned and poolable",
                $"Pool {name} has exactly the declared physical member set",
                "Existing data on the declared physical members becomes inaccessible",
                "Agent live identity, safety, association and poolability verification required")],
            $"Pool {name} contains exactly {members.Count} approved physical members");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateTierTemplate(
        SystemId systemId, StorageObjectId pool, IReadOnlyList<StorageObjectId> members,
        string name, string mediaType, StorageLayoutParameters layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var request = new RealOperationIntentRequest(OperationIntent.CreateStorageTier, systemId,
            PoolTargets(systemId, pool, members),
            [new RealOperationStep("create-tier", new CreateTierCommand(RealTargetReference.ForExisting(pool),
                name, layout.InterleaveBytes, layout.NumberOfColumns, mediaType, layout), [],
                "The complete approved pool member set and the media-specific data members are verified",
                $"Exact {mediaType} template {name} has the requested layout in the pool",
                "Pool-level template metadata is created",
                "Agent live provider media, resiliency, columns, copies, redundancy and interleave support required")],
            $"Exact {mediaType} template {name} with {layout.ResiliencySettingName} layout exists");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest SetPhysicalDiskUsage(
        SystemId systemId, StorageObjectId physical, StorageObjectId pool,
        IReadOnlyList<StorageObjectId> members, string usage)
    {
        var targets = PoolTargets(systemId, pool, members);
        if (!members.Contains(physical))
            throw new ArgumentException("The physical usage target must belong to the complete declared pool member set.", nameof(physical));
        var request = new RealOperationIntentRequest(OperationIntent.SetPhysicalDiskUsage, systemId, targets,
            [new RealOperationStep("set-member-usage", new SetPhysicalDiskUsageCommand(RealTargetReference.ForExisting(physical), usage), [],
                "The exact physical member, complete pool membership and remaining data members are verified",
                $"The exact physical member has Usage {usage}",
                "The physical member's allocation role changes",
                "Agent live usage and pool identity verification required")],
            $"The approved physical member has Usage {usage} in the exact pool");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateTieredVirtualDisk(
        SystemId systemId, StorageObjectId pool, IReadOnlyList<StorageObjectId> members,
        IReadOnlyList<MaximumCapacityTier> tiers, string name)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0 || tiers.Any(tier => tier is null || tier.Tier is null || tier.Tier.Existing is null))
            throw new ArgumentException("Tiered creation needs ordered exact existing templates.", nameof(tiers));
        // Callers can omit the flag for a MAX layer. A sized layer with no flag
        // is made explicitly sized here so a mixed parent cannot change its intent.
        var inputs = tiers.Select(tier => tier.UseMaximumSize is null && tier.SizeBytes is > 0
            ? tier with { UseMaximumSize = false } : tier).ToArray();
        var maximum = inputs.Any(tier => tier.UseMaximumSize ?? true);
        long size;
        try { size = maximum ? 0 : inputs.Sum(tier => tier.SizeBytes ?? 0); }
        catch (OverflowException exception) { throw new ArgumentException("The tier capacities exceed the supported byte range.", nameof(tiers), exception); }
        var targets = PoolTargets(systemId, pool, members).Concat(inputs.Select(tier => tier.Tier.Existing!.Value)).ToArray();
        var request = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk, systemId, targets,
            [new RealOperationStep("create-tiered-vdisk", new CreateTieredVirtualDiskCommand(
                RealTargetReference.ForExisting(pool), inputs[0].Tier, name, size, maximum,
                TieredVirtualDiskCreationMechanism.ExactTemplate, CapacityTiers: inputs,
                Layout: inputs.Length == 1 ? inputs[0].Layout : null), [],
                "Every exact template, complete pool membership and per-layer layout and capacity intent are verified",
                "One virtual disk uses the ordered exact templates with each requested explicit or proven MAX layer capacity",
                "Successful allocations remain in effect if a later layer fails",
                "Agent fresh provider support, frozen MAX origins, durable attempts and exact postconditions required")],
            $"One Fixed virtual disk {name} is bound to the ordered approved templates with the requested layer capacities");
        RealOperationValidator.Validate(request);
        return request;
    }

    private static StorageObjectId[] PoolTargets(SystemId systemId, StorageObjectId pool, IReadOnlyList<StorageObjectId> members)
    {
        ValidateMembers(systemId, members);
        if (pool.System != systemId || pool.Kind != StorageObjectKind.StoragePool)
            throw new ArgumentException("The exact pool must belong to the requested system.", nameof(pool));
        return new[] { pool }.Concat(members).ToArray();
    }

    private static void ValidateMembers(SystemId systemId, IReadOnlyList<StorageObjectId> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0 || members.Any(member => member.System != systemId || member.Kind != StorageObjectKind.PhysicalDisk) ||
            members.Distinct().Count() != members.Count)
            throw new ArgumentException("The complete pool member set must contain distinct physical identities in the requested system.", nameof(members));
    }

    public static RealOperationIntentRequest CreateHddTierTemplate(
        SystemId systemId, StorageObjectId pool, string name)
    {
        var request = OneStep(systemId, OperationIntent.CreateStorageTier, pool,
            new CreateTierCommand(RealTargetReference.ForExisting(pool), name,
                65536, 1),
            $"One 64 KiB, one-column HDD tier template named {name} exists in the exact pool",
            "A pool-level HDD tier template is created; no virtual disk or data volume is created");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateTieredVirtualDisk(
        SystemId systemId, StorageObjectId pool, StorageObjectId tier,
        string name, long sizeBytes, bool useMaximumSize = false)
    {
        var capacity = CreationCapacity(sizeBytes, useMaximumSize);
        var request = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk,
            systemId, [pool, tier],
            [new RealOperationStep("create-tiered-vdisk",
                new CreateTieredVirtualDiskCommand(
                    RealTargetReference.ForExisting(pool),
                    RealTargetReference.ForExisting(tier), name, sizeBytes, useMaximumSize,
                    TieredVirtualDiskCreationMechanism.ExactTemplate), [],
                "The exact single-member pool, sole unused HDD template, and supported Simple creation size are verified",
                $"One Simple Fixed virtual disk named {name} is created on the exact HDD template with capacity {capacity}",
                "The requested capacity is allocated from the exact pool",
                "Agent live provider and exact-template size verification required")],
            $"One Simple Fixed virtual disk {name} ({capacity}) is bound to the exact existing HDD template");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest RenameHddTier(
        SystemId systemId, StorageObjectId tier, string name,
        StorageObjectId? pool = null, IReadOnlyList<StorageObjectId>? members = null)
    {
        var request = OneStep(systemId, OperationIntent.RenameStorageObject, tier,
            new RenameTierCommand(RealTargetReference.ForExisting(tier), name),
            $"The exact HDD tier retains its identity and associations with name {name}",
            "No data loss expected");
        request = DeclarePoolMembers(request, pool, members);
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateMultiTieredMaximumVirtualDisk(
        SystemId systemId, StorageObjectId pool, IReadOnlyList<StorageObjectId> tiers, string name)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count < 2 || tiers.Distinct().Count() != tiers.Count)
            throw new ArgumentException("Multi-tier MAX requires distinct ordered templates.", nameof(tiers));
        var inputs = tiers.Select(tier => new MaximumCapacityTier(RealTargetReference.ForExisting(tier), null)).ToArray();
        var request = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk, systemId,
            new[] { pool }.Concat(tiers).ToArray(),
            [new RealOperationStep("create-tiered-vdisk", new CreateTieredVirtualDiskCommand(
                RealTargetReference.ForExisting(pool), inputs[0].Tier, name, 0, true,
                TieredVirtualDiskCreationMechanism.ExactTemplate, CapacityTiers: inputs), [],
                "Exact unused templates, complete approved member set and per-tier layouts are verified",
                "One VD is seeded at 0.5 C per tier, then every actual tier reaches its proven whole-GiB maximum in the listed order",
                "All successful allocation and growth remains in effect if later search stops",
                "Agent-frozen per-tier origins, durable attempts and strict postconditions required")],
            "One exact multi-tier VD with verified whole-GiB maximum for every actual tier");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest DeleteHddTierTemplate(
        SystemId systemId, StorageObjectId tier,
        StorageObjectId? pool = null, IReadOnlyList<StorageObjectId>? members = null)
    {
        var request = OneStep(systemId, OperationIntent.DeleteStorageTier, tier,
            new DeleteTierCommand(RealTargetReference.ForExisting(tier)),
            "The exact unused HDD tier template is absent",
            "The pool-level HDD template metadata is removed; no virtual disk or data volume is deleted");
        request = DeclarePoolMembers(request, pool, members);
        RealOperationValidator.Validate(request);
        return request;
    }

    private static RealOperationIntentRequest DeclarePoolMembers(RealOperationIntentRequest request,
        StorageObjectId? pool, IReadOnlyList<StorageObjectId>? members)
    {
        if (pool is null && members is null) return request;
        if (pool is null || members is null)
            throw new ArgumentException("An explicit pool and its complete physical member set must be supplied together.");
        return request with { Targets = request.Targets.Concat(PoolTargets(request.SystemId, pool.Value, members)).ToArray() };
    }

    public static RealOperationIntentRequest DissolveSingleMemberPool(
        SystemId systemId, StorageObjectId pool, StorageObjectId? virtualDisk,
        IReadOnlyList<StorageObjectId>? poolTierTemplates = null,
        IReadOnlyList<StorageObjectId>? members = null)
    {
        poolTierTemplates ??= [];
        if (pool.System != systemId || pool.Kind != StorageObjectKind.StoragePool
            || (virtualDisk is { } vdisk
                && (vdisk.System != systemId || vdisk.Kind != StorageObjectKind.VirtualDisk))
            || poolTierTemplates.Any(tier => tier.System != systemId
                || tier.Kind != StorageObjectKind.StorageTier)
            || poolTierTemplates.Distinct().Count() != poolTierTemplates.Count)
            throw new ArgumentException("Dissolve targets must be unique exact existing children of the requested pool.");

        var targets = new List<StorageObjectId> { pool };
        if (members is not null)
        {
            ValidateMembers(systemId, members);
            targets.AddRange(members);
        }
        var steps = new List<RealOperationStep>();
        string? previous = null;
        if (virtualDisk is { } existingVdisk)
        {
            targets.Add(existingVdisk);
            steps.Add(new RealOperationStep("delete-vdisk",
                new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(existingVdisk)), [],
                "The exact virtual disk and its child objects are verified",
                "The virtual disk, its child volumes, and its virtual-disk tier instances are absent",
                "All files, partitions, volumes, and drive letters on the virtual disk are lost",
                "Agent live Windows removal check required"));
            previous = "delete-vdisk";
        }

        for (var index = 0; index < poolTierTemplates.Count; index++)
        {
            var tier = poolTierTemplates[index];
            var stepId = $"delete-tier-{index + 1}";
            steps.Add(new RealOperationStep(stepId,
                new DeleteTierCommand(RealTargetReference.ForExisting(tier)),
                previous is null ? [] : [previous],
                "The exact virtual disk is absent and this exact pool-level HDD template is unused",
                "The exact pool-level HDD template is absent",
                "The pool-level HDD template metadata is removed",
                "Agent live Windows tier-removal check required"));
            targets.Add(tier);
            previous = stepId;
        }

        steps.Add(new RealOperationStep("delete-pool",
            new DeletePoolCommand(RealTargetReference.ForExisting(pool)),
            previous is null ? [] : [previous],
            "The pool has no remaining virtual disk or pool-level tier template",
            "The pool is absent and its physical member is released",
            "All remaining pool metadata is removed",
            "Agent live Windows removal check required"));
        var request = new RealOperationIntentRequest(OperationIntent.DeleteStoragePool,
            systemId, targets, steps,
            "Delete the exact pool after its virtual disk and explicit pool-level HDD templates are removed");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest CreateSingleMemberPool(
        SystemId systemId, StorageObjectId physicalDisk, string poolName,
        VirtualDiskOptions? virtualDisk)
    {
        var steps = new List<RealOperationStep>
        {
            new("create-pool",
                new CreatePoolCommand(RealTargetReference.ForExisting(physicalDisk),
                    poolName), [],
                "The exact physical disk is RAW, has zero partitions and can join a pool",
                $"One-member storage pool {poolName} exists",
                "Existing partitions and data on the physical disk become inaccessible",
                "Agent live Windows poolability check required")
        };
        if (virtualDisk is not null)
            AppendVirtualDiskSteps(steps,
                RealTargetReference.FromStep(StorageObjectKind.StoragePool,
                    "create-pool"), virtualDisk);

        var expected = virtualDisk is null
            ? $"One-member storage pool named {poolName}"
            : $"One-member storage pool {poolName}; Simple Fixed one-column " +
              $"virtual disk {virtualDisk.Name} ({CreationCapacity(virtualDisk.SizeBytes, virtualDisk.UseMaximumSize)})" +
              (virtualDisk.InitializeAndPartition
                  ? "; GPT metadata only, then provider-created MSR and data layout are handled after a fresh read"
                  : "; virtual disk remains RAW without partitions");
        var request = new RealOperationIntentRequest(OperationIntent.CreateStoragePool,
            systemId, [physicalDisk], steps, expected);
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest RebuildSingleMemberPool(
        SystemId systemId, StorageObjectId physicalDisk,
        StorageObjectId oldPool, StorageObjectId oldVirtualDisk,
        string newPoolName, VirtualDiskOptions options,
        IReadOnlyList<StorageObjectId>? oldTierTemplates = null,
        IReadOnlyList<StorageObjectId>? members = null)
    {
        oldTierTemplates ??= [];
        if (oldTierTemplates.Any(tier => tier.System != systemId
            || tier.Kind != StorageObjectKind.StorageTier)
            || oldTierTemplates.Distinct().Count() != oldTierTemplates.Count)
            throw new ArgumentException("Rebuild tiers must be unique exact existing tier targets in the requested system.", nameof(oldTierTemplates));
        var steps = new List<RealOperationStep>
        {
            new("delete-vdisk",
                new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(oldVirtualDisk)),
                [], "The exact old virtual disk and every child are verified",
                "The old virtual disk and its child volumes are absent",
                "All files, partitions, volumes and drive letters on the old virtual disk are lost",
                "Agent live Windows removal check required"),
        };
        var previous = "delete-vdisk";
        for (var index = 0; index < oldTierTemplates.Count; index++)
        {
            var tier = oldTierTemplates[index];
            var stepId = $"delete-tier-{index + 1}";
            steps.Add(new RealOperationStep(stepId,
                new DeleteTierCommand(RealTargetReference.ForExisting(tier)), [previous],
                "The exact old virtual disk has been verified absent and this exact pool-level HDD template is unused",
                "The exact pool-level HDD template is absent",
                "The old pool-level HDD template metadata is removed",
                "Agent live Windows tier-removal check required"));
            previous = stepId;
        }
        steps.Add(new RealOperationStep("delete-pool",
                new DeletePoolCommand(RealTargetReference.ForExisting(oldPool)),
                [previous],
                "The old pool has no remaining virtual disks or tier templates",
                "The old pool is absent and its physical member is released",
                "All old pool metadata is lost",
                "Agent live Windows removal check required"));
        var targets = new List<StorageObjectId> { physicalDisk, oldPool, oldVirtualDisk };
        if (members is not null)
        {
            ValidateMembers(systemId, members);
            if (!members.Contains(physicalDisk))
                throw new ArgumentException("The physical anchor must belong to the complete approved rebuild member set.", nameof(members));
            targets.AddRange(members.Where(member => member != physicalDisk));
        }
        targets.AddRange(oldTierTemplates);
        var request = new RealOperationIntentRequest(OperationIntent.RebuildStoragePool,
            systemId, targets, steps,
            $"Remove the old pool and virtual disk before separately preparing {newPoolName}/{options.Name}; " +
            $"requested replacement is Simple Fixed one-column {CreationCapacity(options.SizeBytes, options.UseMaximumSize)}. " +
            "Released-disk clearing and replacement creation require fresh identities and separate frozen confirmations");
        RealOperationValidator.Validate(request);
        return request;
    }

    private static string CreationCapacity(long bytes, bool useMaximum) =>
        useMaximum ? "WinPool MAX: subtract 4,000,000 bytes, take the strictly smaller whole GiB, then search in 1 GiB steps" : $"{bytes} bytes";

    public static void AppendVirtualDiskSteps(
        List<RealOperationStep> steps, RealTargetReference pool,
        VirtualDiskOptions options)
    {
        var previous = steps.Count == 0 ? null : steps[^1].Id;
        var layout = options.Layout ?? StorageLayoutParameters.LegacyDefault;
        steps.Add(new RealOperationStep("create-vdisk",
            new CreateVirtualDiskCommand(pool, options.Name, options.SizeBytes,
                layout.InterleaveBytes, layout.NumberOfColumns, options.UseMaximumSize, Layout: options.Layout), previous is null ? [] : [previous],
            options.UseMaximumSize ? "The exact complete pool member set and requested layout are verified; Agent freezes the whole-GiB MAX search from fresh facts"
                : "The exact complete pool member set, requested layout and supported creation size are verified",
            $"One {layout.ResiliencySettingName} Fixed virtual disk exists in the pool",
            "The requested capacity is allocated from the pool",
            options.UseMaximumSize ? "Journaled explicit-size creation and growth; verified unchanged capacity rejection proves the maximum boundary"
                : "Agent live pool size support required"));
        if (!options.InitializeAndPartition)
            return;

        var osDisk = RealTargetReference.FromStep(StorageObjectKind.OsDisk, "create-vdisk");
        steps.Add(new RealOperationStep("initialize-vdisk",
            new InitializeGptCommand(osDisk), ["create-vdisk"],
            "The new OS disk is RAW and has been identified by the prior step",
            "The new OS disk has GPT metadata; any provider-created MSR is read before a separate layout plan",
            "New GPT metadata replaces any data in the selected virtual disk",
            "Agent live Windows preflight required"));
    }
}
