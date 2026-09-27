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
        SystemId systemId, StorageObjectId osDisk, bool createMsr)
    {
        var target = RealTargetReference.ForExisting(osDisk);
        var steps = new List<RealOperationStep>
        {
            new("initialize-gpt", new InitializeGptCommand(target), [],
                "The exact OS disk is RAW and has zero partitions",
                "The exact disk is GPT", "GPT metadata replaces any prior table",
                "Agent live Windows preflight required")
        };
        if (createMsr)
        {
            steps.Add(new RealOperationStep("create-msr",
                new CreatePartitionCommand(target, RealPartitionRole.Msr,
                    1024L * 1024, 16L * 1024 * 1024), ["initialize-gpt"],
                "The new GPT disk has a free 16 MiB range at 1 MiB",
                "A 16 MiB Microsoft Reserved partition exists",
                "The reserved range is no longer available for data",
                "Agent live Windows geometry check required"));
        }
        var request = new RealOperationIntentRequest(OperationIntent.InitializeDisk,
            systemId, [osDisk], steps,
            createMsr ? "GPT disk with one 16 MiB MSR at 1 MiB" : "GPT disk with zero partitions");
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
            _ => (RealFileSystem?)null
        };
        if (format is null)
            return null;
        var request = OneStep(systemId, OperationIntent.FormatVolume, partition,
            new FormatVolumeCommand(RealTargetReference.ForExisting(partition),
                format.Value, clusterBytes, fullFormat, label),
            $"Partition formatted as {format.Value}",
            "All existing files and volume data on the selected partition are erased");
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
            $"Simple Fixed one-column virtual disk {options.Name} ({options.SizeBytes} bytes)");
        RealOperationValidator.Validate(request);
        return request;
    }

    public static RealOperationIntentRequest RebuildSingleMemberPool(
        SystemId systemId, StorageObjectId physicalDisk,
        StorageObjectId oldPool, StorageObjectId oldVirtualDisk,
        string newPoolName, VirtualDiskOptions options)
    {
        var steps = new List<RealOperationStep>
        {
            new("delete-vdisk",
                new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(oldVirtualDisk)),
                [], "The exact old virtual disk and every child are verified",
                "The old virtual disk and its child volumes are absent",
                "All files, partitions, volumes and drive letters on the old virtual disk are lost",
                "Agent live Windows removal check required"),
            new("delete-pool",
                new DeletePoolCommand(RealTargetReference.ForExisting(oldPool)),
                ["delete-vdisk"],
                "The old pool has no remaining virtual disk",
                "The old pool is absent and its physical member is released",
                "All old pool metadata is lost",
                "Agent live Windows removal check required"),
            new("create-pool",
                new CreatePoolCommand(RealTargetReference.ForExisting(physicalDisk),
                    newPoolName), ["delete-pool"],
                "The exact physical member is RAW and poolable after old-pool removal",
                "A new one-member pool exists with a new identity",
                "The old pool identity cannot be restored by this operation",
                "Agent live Windows poolability check required")
        };
        AppendVirtualDiskSteps(steps,
            RealTargetReference.FromStep(StorageObjectKind.StoragePool, "create-pool"),
            options);
        var request = new RealOperationIntentRequest(OperationIntent.RebuildStoragePool,
            systemId, [physicalDisk, oldPool, oldVirtualDisk], steps,
            $"Replace the old pool and virtual disk with {newPoolName}/{options.Name}; " +
            $"Simple Fixed one-column {options.SizeBytes} bytes");
        RealOperationValidator.Validate(request);
        return request;
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
            "The new OS disk has GPT metadata",
            "New GPT metadata replaces any data in the selected virtual disk",
            "Agent live Windows preflight required"));
        previous = "initialize-vdisk";
        var dataOffset = 1024L * 1024;
        if (options.CreateMsr)
        {
            steps.Add(new RealOperationStep("create-msr",
                new CreatePartitionCommand(osDisk, RealPartitionRole.Msr,
                    dataOffset, 16L * 1024 * 1024),
                ["create-vdisk", previous],
                "The new GPT disk has a free 16 MiB range",
                "A 16 MiB Microsoft Reserved partition exists",
                "The reserved range is no longer available for data",
                "Agent live Windows geometry check required"));
            previous = "create-msr";
            dataOffset += 16L * 1024 * 1024;
        }
        var dataSize = (options.SizeBytes - dataOffset - 1024L * 1024)
            / (1024L * 1024) * (1024L * 1024);
        steps.Add(new RealOperationStep("create-data",
            new CreatePartitionCommand(osDisk, RealPartitionRole.BasicData,
                dataOffset, dataSize),
            options.CreateMsr
                ? ["create-vdisk", "initialize-vdisk", previous]
                : ["create-vdisk", "initialize-vdisk"],
            "The new GPT disk has the requested free range",
            "A BasicData partition occupies the range",
            "Data in the selected range becomes inaccessible",
            "Agent live Windows geometry check required"));
        previous = "create-data";
        var partition = RealTargetReference.FromStep(StorageObjectKind.Partition,
            "create-data");
        if (options.FormatNtfs)
        {
            steps.Add(new RealOperationStep("format-data",
                new FormatVolumeCommand(partition, RealFileSystem.Ntfs,
                    65536, false, options.Label), [previous],
                "The new BasicData partition exists",
                "The partition is quickly formatted NTFS with 64 KiB clusters",
                "Formatting erases all data in the new partition range",
                "Agent live NTFS support required"));
            previous = "format-data";
        }
        if (options.Letter is { } letter)
        {
            steps.Add(new RealOperationStep("assign-data-letter",
                new SetDriveLetterCommand(partition, null, letter),
                previous == "create-data" ? ["create-data"]
                    : ["create-data", previous],
                "The new partition has no drive letter",
                "The selected drive letter is assigned",
                "No additional data loss expected",
                "Agent live drive-letter check required"));
        }
    }
}
