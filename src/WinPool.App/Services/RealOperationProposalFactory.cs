using System.Text.Json;
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
        bool CreateMsr, bool FormatNtfs, string? Label, char? Letter);

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
        if (virtualDiskSizeBytes is <= 0 ||
            (virtualDiskSizeBytes is long requestedSize && requestedSize % mib != 0) ||
            (formatNtfs && virtualDiskSizeBytes is null) ||
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

        if (virtualDiskSizeBytes is long sizeBytes)
        {
            var dataOffset = createMsr ? 17 * mib : mib;
            var geometry = EditWorkspace.GetRealPartitionCreateGeometry(sizeBytes,
                dataOffset, sizeBytes - dataOffset);
            if (geometry is not { CanCreate: true, StartOffsetBytes: long start, MaximumSizeBytes: long dataSize }
                || start != dataOffset)
                throw new ArgumentException("The requested virtual-disk size cannot hold the selected MSR layout and a BasicData partition.", nameof(virtualDiskSizeBytes));
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
            $"Simple Fixed one-column virtual disk {options.Name} ({options.SizeBytes} bytes)" +
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
        string name, long sizeBytes)
    {
        var request = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk,
            systemId, [pool, tier],
            [new RealOperationStep("create-tiered-vdisk",
                new CreateTieredVirtualDiskCommand(
                    RealTargetReference.ForExisting(pool),
                    RealTargetReference.ForExisting(tier), name, sizeBytes), [],
                "The exact single-member pool, sole unused HDD template, and supported Simple creation size are verified",
                $"One Simple Fixed virtual disk named {name} is created on the exact HDD template with size {sizeBytes} bytes",
                "The requested capacity is allocated from the exact pool",
                "Agent live provider and exact-template size verification required")],
            $"One Simple Fixed virtual disk {name} ({sizeBytes} bytes) is bound to the exact existing HDD template");
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
              $"virtual disk {virtualDisk.Name} ({virtualDisk.SizeBytes} bytes)" +
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
            $"requested replacement is Simple Fixed one-column {options.SizeBytes} bytes. " +
            "Released-disk clearing and replacement creation require fresh identities and separate frozen confirmations");
        RealOperationValidator.Validate(request);
        return request;
    }

    // Each submit is the ordinary Prepare + frozen confirmation + Accept UI flow.
    // A false result includes cancellation, failure and outcome unknown; never continue it.
    public static async Task<bool> ExecuteSingleMemberPoolRebuildAsync(
        WinPoolFacts originalFacts, StorageObjectId physicalDisk,
        StorageObjectId oldPool, StorageObjectId oldVirtualDisk,
        string newPoolName, VirtualDiskOptions options,
        IReadOnlyList<StorageObjectId> oldTierTemplates,
        Func<RealOperationIntentRequest, Task<bool>> submit,
        Func<WinPoolFacts?> readFreshFacts,
        Func<WinPoolFacts, OsDiskInfo, Task<bool>> confirmClear)
    {
        var identity = RequireRebuildMemberIdentity(originalFacts, physicalDisk);
        // Validate requested creation values before any destructive stage; this is not Prepare/Accept.
        _ = CreateSingleMemberPool(originalFacts.SystemId, physicalDisk, newPoolName, options);
        var deletion = RebuildSingleMemberPool(originalFacts.SystemId, physicalDisk,
            oldPool, oldVirtualDisk, newPoolName, options, oldTierTemplates);
        if (!await submit(deletion))
            return false;

        var releasedFacts = readFreshFacts()
            ?? throw new InvalidDataException("A fresh released-member scan is required before rebuilding.");
        RequireNewRebuildInventory(originalFacts, releasedFacts);
        var releasedDisk = RequireReleasedRebuildDisk(releasedFacts, physicalDisk,
            identity, oldPool, oldVirtualDisk);
        var releasedSnapshot = WinPoolStorageProjection.Project(releasedFacts);
        if (!IsPartitionFreeRaw(releasedFacts, releasedSnapshot, releasedDisk))
        {
            if (!await confirmClear(releasedFacts, releasedDisk))
                return false;
            var clear = ClearToRaw(releasedFacts.SystemId,
                new StorageObjectId(releasedFacts.SystemId, StorageObjectKind.OsDisk, releasedDisk.StableId));
            if (!await submit(clear))
                return false;
            var rawFacts = readFreshFacts()
                ?? throw new InvalidDataException("A fresh RAW-member scan is required before pool creation.");
            RequireNewRebuildInventory(releasedFacts, rawFacts);
            releasedDisk = RequireReleasedRebuildDisk(rawFacts, physicalDisk,
                identity, oldPool, oldVirtualDisk);
            if (!IsPartitionFreeRaw(rawFacts, WinPoolStorageProjection.Project(rawFacts), releasedDisk))
                throw new InvalidDataException("The separately cleared exact member is not partition-free RAW.");
        }

        return await submit(CreateSingleMemberPool(originalFacts.SystemId,
            physicalDisk, newPoolName, options));
    }

    private sealed record RebuildMemberIdentity(string UniqueId, string ObjectId, string SerialNumber);

    private static RebuildMemberIdentity RequireRebuildMemberIdentity(
        WinPoolFacts facts, StorageObjectId physicalDisk)
    {
        if (facts.IsSimulation || facts.SystemId != physicalDisk.System
            || physicalDisk.Kind != StorageObjectKind.PhysicalDisk)
            throw new InvalidDataException("Rebuilding requires the exact current local physical member.");
        var matches = facts.Objects.Where(item => item.Id == physicalDisk.ProviderKey
            && item.ObjectType == FactObjectType.PhysicalDisk && item.HasReliableIdentity).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The physical rebuild member cannot be identified uniquely.");
        RequireRebuildSource(facts, matches[0], "MSFT_PhysicalDisk");
        return new RebuildMemberIdentity(RebuildText(matches[0], "UniqueId"),
            RebuildText(matches[0], "ObjectId"), RebuildText(matches[0], "SerialNumber"));
    }

    private static OsDiskInfo RequireReleasedRebuildDisk(WinPoolFacts facts,
        StorageObjectId physicalDisk, RebuildMemberIdentity identity,
        StorageObjectId oldPool, StorageObjectId oldVirtualDisk)
    {
        if (RequireRebuildMemberIdentity(facts, physicalDisk) != identity)
            throw new InvalidDataException("The released physical member identity changed.");
        foreach (var className in new[] { "MSFT_StoragePool", "MSFT_StorageTier", "MSFT_VirtualDisk",
                     "MSFT_Disk", "MSFT_Partition", "MSFT_Volume" })
            RequireRebuildSource(facts, null, className);
        if (facts.Objects.Any(item => item.Id == oldPool.ProviderKey || item.Id == oldVirtualDisk.ProviderKey))
            throw new InvalidDataException("The old pool or virtual disk is still present.");
        var snapshot = WinPoolStorageProjection.Project(facts);
        if (snapshot.StoragePools.Any(pool => !pool.IsPrimordial
            && pool.MemberPhysicalDiskIds.Contains(physicalDisk.ProviderKey, StringComparer.Ordinal)))
            throw new InvalidDataException("The released member still belongs to a non-primordial pool.");
        var members = snapshot.PhysicalDisks.Where(item => item.StableId == physicalDisk.ProviderKey).ToArray();
        if (members.Length != 1 || !members[0].IsStable || !members[0].CanPool
            || members[0].IsBoot || members[0].IsSystem || members[0].IsPageFile
            || members[0].IsCrashDump || members[0].IsRetired || members[0].IsHotSpare)
            throw new InvalidDataException("The released member is not safely poolable.");
        var diskRelations = facts.Relationships.Where(item => item.FromId == physicalDisk.ProviderKey
            && item.Kind == "same-device").ToArray();
        if (diskRelations.Length != 1 || diskRelations[0].IsRetained
            || diskRelations[0].ObservedAt != facts.InventoryCapturedAt)
            throw new InvalidDataException("The released member lacks one current direct OS disk association.");
        var diskId = diskRelations[0].ToId;
        if (facts.Relationships.Count(item => item.ToId == diskId && item.Kind == "same-device") != 1)
            throw new InvalidDataException("The released OS disk has an ambiguous device parent.");
        var diskObjects = facts.Objects.Where(item => item.Id == diskId
            && item.ObjectType == FactObjectType.Disk && item.HasReliableIdentity).ToArray();
        if (diskObjects.Length != 1)
            throw new InvalidDataException("The released OS disk lacks a reliable current identity.");
        RequireRebuildSource(facts, diskObjects[0], "MSFT_Disk");
        _ = RebuildText(diskObjects[0], "UniqueId");
        _ = RebuildText(diskObjects[0], "ObjectId");
        _ = RebuildText(diskObjects[0], "Path");
        foreach (var fieldName in new[] { "IsBoot", "IsSystem", "IsOffline", "IsReadOnly" })
            if (diskObjects[0].Field(fieldName) is not
                { ReadState: FieldReadState.Returned, ValueType: FactValueType.Boolean,
                    Value: { ValueKind: JsonValueKind.False } })
                throw new InvalidDataException($"The released OS disk lacks a safe {fieldName} observation.");
        if (snapshot.UnknownTierMembershipPhysicalDiskIds.Contains(physicalDisk.ProviderKey,
            StringComparer.Ordinal))
            throw new InvalidDataException("The released member has unknown tier membership.");
        var disks = snapshot.OsDisks.Where(item => item.StableId == diskId).ToArray();
        if (disks.Length != 1 || disks[0].PhysicalDiskStableId != physicalDisk.ProviderKey
            || disks[0].VirtualDiskStableId is not null || disks[0].IsBoot
            || disks[0].IsSystem || disks[0].IsOffline)
            throw new InvalidDataException("The released OS disk is not the safe direct physical member.");
        var sourceDisk = diskObjects[0];
        if (sourceDisk.Field("PartitionStyle") is not { } styleField
            || !styleField.TryGetInt64(out var style) || style is not (0 or 2)
            || sourceDisk.Field("NumberOfPartitions") is not { } countField
            || !countField.TryGetInt64(out var count) || count < 0
            || count != snapshot.Partitions.Count(item => item.OsDiskStableId == diskId))
            throw new InvalidDataException("The released member lacks a complete RAW/GPT partition layout.");
        return disks[0];
    }

    private static bool IsPartitionFreeRaw(WinPoolFacts facts, StorageSnapshot snapshot, OsDiskInfo disk)
    {
        var sourceDisk = facts.Objects.Single(item => item.Id == disk.StableId);
        var path = RebuildText(sourceDisk, "Path");
        return disk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
            && sourceDisk.Field("PartitionStyle") is { } styleField
            && styleField.TryGetInt64(out var style) && style == 0
            && sourceDisk.Field("NumberOfPartitions") is { } countField
            && countField.TryGetInt64(out var count) && count == 0
            && !snapshot.Partitions.Any(item => item.OsDiskStableId == disk.StableId)
            && !snapshot.UnattachedPartitions.Any(item => item.OsDiskId == disk.StableId)
            && !facts.Objects.Any(item => item.ObjectType == FactObjectType.Partition
                && item.Field("DiskId")?.DisplayValue() == path);
    }

    private static void RequireNewRebuildInventory(WinPoolFacts before, WinPoolFacts after)
    {
        if (after.IsSimulation || after.SystemId != before.SystemId
            || string.IsNullOrWhiteSpace(after.InventoryVersion)
            || after.InventoryVersion == before.InventoryVersion
            || after.InventoryCapturedAt <= before.InventoryCapturedAt)
            throw new InvalidDataException("Rebuild continuation requires a newer exact local inventory.");
    }

    private static void RequireRebuildSource(WinPoolFacts facts,
        WinPoolSourceObject? item, string className)
    {
        var sources = facts.Sources.Where(source => source.Origin == FactOrigin.StorageCim
            && source.Namespace.Equals("root/microsoft/windows/storage", StringComparison.OrdinalIgnoreCase)
            && source.ClassName == className).ToArray();
        if (sources.Length != 1 || sources[0].ReadState != FieldReadState.Returned
            || sources[0].CapturedAt != facts.InventoryCapturedAt
            || (item is not null && item.SourceRef != sources[0].Id))
            throw new InvalidDataException($"Rebuild continuation lacks a complete current {className} source.");
    }

    private static string RebuildText(WinPoolSourceObject item, string name)
    {
        var field = item.Field(name);
        return field is { ReadState: FieldReadState.Returned, ValueType: FactValueType.String,
            Value: { ValueKind: JsonValueKind.String } value }
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidDataException($"The exact rebuild target lacks {name}.");
    }

    public static void AppendVirtualDiskSteps(
        List<RealOperationStep> steps, RealTargetReference pool,
        VirtualDiskOptions options)
    {
        var previous = steps.Count == 0 ? null : steps[^1].Id;
        steps.Add(new RealOperationStep("create-vdisk",
            new CreateVirtualDiskCommand(pool, options.Name, options.SizeBytes,
                65536, 1), previous is null ? [] : [previous],
            "The exact single-member pool and supported creation size are verified",
            "One Simple Fixed virtual disk exists in the pool",
            "The requested capacity is allocated from the pool",
            "Agent live pool size support required"));
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
