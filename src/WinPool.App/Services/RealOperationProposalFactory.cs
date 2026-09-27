using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>
/// App-side proposals contain requested values only. The Agent rereads Windows
/// facts and replaces every condition, impact statement and support assertion.
/// </summary>
public static class RealOperationProposalFactory
{
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

    public static RealOperationIntentRequest CreatePartition(
        SystemId systemId, StorageObjectId disk,
        RealPartitionRole role, long offsetBytes, long sizeBytes,
        RealFileSystem? fileSystem, int clusterBytes, bool fullFormat,
        string? label, char? letter)
    {
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
}
