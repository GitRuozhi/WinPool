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
        bool CreateMsr, bool FormatNtfs, string? Label, char? Letter, bool UseMaximumSize = false);

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
        bool formatNtfs = false, string? label = null, char? letter = null)
    {
        const long mib = 1024L * 1024;
        const long msrOffset = mib;
        const long msrSize = 16 * mib;
        (long OffsetBytes, long SizeBytes)? dataGeometry = virtualDiskSizeBytes is long requestedSize
            ? GetAutomaticDataGeometry(requestedSize, createMsr) : null;
        if ((formatNtfs && virtualDiskSizeBytes is null) ||
            ((formatNtfs || letter is not null) && virtualDiskSizeBytes is null) ||
            (letter is { } driveLetter && (driveLetter < 'D' || driveLetter > 'Z')))
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
                    new FormatVolumeCommand(partition, RealFileSystem.Ntfs,
                        65536, false, label), [previous],
                    "The new BasicData partition exists",
                    "The partition is quickly formatted NTFS with 64 KiB clusters",
                    "Formatting erases all data in the new partition range",
                    "Agent live NTFS support required"));
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
            : $"Initialized GPT disk with {(createMsr ? "an MSR and " : string.Empty)}BasicData partition; optional NTFS format and drive letter are explicit";
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
        const long mib = 1024L * 1024;
        if (sizeBytes <= 0 || sizeBytes % mib != 0)
            throw new ArgumentException("Automatic partition layout needs a positive whole-MiB virtual-disk size.", nameof(sizeBytes));
        var offset = createMsr ? 17 * mib : mib;
        var geometry = EditWorkspace.GetRealPartitionCreateGeometry(sizeBytes, offset, sizeBytes - offset);
        if (geometry is not { CanCreate: true, StartOffsetBytes: long start, MaximumSizeBytes: long size }
            || start != offset)
            throw new ArgumentException("The requested virtual-disk size cannot hold the selected MSR layout and a BasicData partition.", nameof(sizeBytes));
        return (offset, size);
    }

    public static RealOperationIntentRequest? TryFormatExistingData(
        SystemId systemId, StorageObjectId partition, string? fileSystem,
        int clusterBytes, bool fullFormat, string? label)
    {
        var format = fileSystem?.ToUpperInvariant() switch
        {
            "NTFS" => RealFileSystem.Ntfs,
            "EXFAT" => RealFileSystem.ExFat,
            "REFS" when clusterBytes == 65536 && !fullFormat => RealFileSystem.ReFs,
            _ => (RealFileSystem?)null
        };
        if (format is null)
            return null;
        var request = OneStep(systemId, OperationIntent.FormatVolume, partition,
            new FormatVolumeCommand(RealTargetReference.ForExisting(partition),
                format.Value, clusterBytes, fullFormat, label),
            $"Partition formatted as {format.Value}",
            format == RealFileSystem.ReFs
                ? "All existing files and volume data on the selected partition are erased; ReFS has no long-run evidence equivalent to 64 KiB NTFS"
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
        if (fileSystem == RealFileSystem.ReFs)
            throw new ArgumentException(
                "Real ReFS creation remains disabled until C01 has current provider evidence.",
                nameof(fileSystem));
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
            $"Simple Fixed one-column virtual disk {options.Name} ({CreationCapacity(options.SizeBytes, options.UseMaximumSize)})" +
            (options.InitializeAndPartition
                ? "; GPT metadata only, with provider-created MSR and data layout handled after a fresh read"
                : "; virtual disk remains RAW without partitions"));
        RealOperationValidator.Validate(request);
        return request;
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
        SystemId systemId, StorageObjectId tier, string name)
    {
        var request = OneStep(systemId, OperationIntent.RenameStorageObject, tier,
            new RenameTierCommand(RealTargetReference.ForExisting(tier), name),
            $"The exact HDD tier retains its identity and associations with name {name}",
            "No data loss expected");
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
        SystemId systemId, StorageObjectId tier)
    {
        var request = OneStep(systemId, OperationIntent.DeleteStorageTier, tier,
            new DeleteTierCommand(RealTargetReference.ForExisting(tier)),
            "The exact unused HDD tier template is absent",
            "The pool-level HDD template metadata is removed; no virtual disk or data volume is deleted");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest DissolveSingleMemberPool(
        SystemId systemId, StorageObjectId pool, StorageObjectId? virtualDisk,
        IReadOnlyList<StorageObjectId>? poolTierTemplates = null)
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
        IReadOnlyList<StorageObjectId>? oldTierTemplates = null)
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
        steps.Add(new RealOperationStep("create-vdisk",
            new CreateVirtualDiskCommand(pool, options.Name, options.SizeBytes,
                65536, 1, options.UseMaximumSize), previous is null ? [] : [previous],
            options.UseMaximumSize ? "The exact single-member empty pool and fixed layout are verified; Agent freezes the whole-GiB MAX search from fresh facts"
                : "The exact single-member pool and supported creation size are verified",
            "One Simple Fixed virtual disk exists in the pool",
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
