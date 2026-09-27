using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Execution;

// These commands are data contracts. Only an explicit adapter dispatch may execute them.
// In particular, descriptions and simulation preview text are never executable input.
public enum RealFileSystem { Ntfs, ExFat, ReFs, Fat32 }
public enum RealPartitionRole { BasicData, Efi, Msr, Recovery }

public sealed record RealTargetReference(
    StorageObjectKind Kind,
    StorageObjectId? Existing,
    string? CreatedByStep)
{
    public static RealTargetReference ForExisting(StorageObjectId id) => new(id.Kind, id, null);
    public static RealTargetReference FromStep(StorageObjectKind kind, string stepId) => new(kind, null, stepId);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$command")]
[JsonDerivedType(typeof(SetDiskOnlineCommand), "set-disk-online")]
[JsonDerivedType(typeof(InitializeGptCommand), "initialize-gpt")]
[JsonDerivedType(typeof(ClearDiskCommand), "clear-disk")]
[JsonDerivedType(typeof(CreatePartitionCommand), "create-partition")]
[JsonDerivedType(typeof(DeletePartitionCommand), "delete-partition")]
[JsonDerivedType(typeof(ResizePartitionCommand), "resize-partition")]
[JsonDerivedType(typeof(FormatVolumeCommand), "format-volume")]
[JsonDerivedType(typeof(SetDriveLetterCommand), "set-drive-letter")]
[JsonDerivedType(typeof(RenameVolumeCommand), "rename-volume")]
[JsonDerivedType(typeof(CreatePoolCommand), "create-pool")]
[JsonDerivedType(typeof(DeletePoolCommand), "delete-pool")]
[JsonDerivedType(typeof(RenamePoolCommand), "rename-pool")]
[JsonDerivedType(typeof(CreateVirtualDiskCommand), "create-virtual-disk")]
[JsonDerivedType(typeof(DeleteVirtualDiskCommand), "delete-virtual-disk")]
[JsonDerivedType(typeof(ResizeVirtualDiskCommand), "resize-virtual-disk")]
[JsonDerivedType(typeof(RenameVirtualDiskCommand), "rename-virtual-disk")]
[JsonDerivedType(typeof(CreateTierCommand), "create-tier")]
[JsonDerivedType(typeof(CreateTieredVirtualDiskCommand), "create-tiered-virtual-disk")]
[JsonDerivedType(typeof(DeleteTierCommand), "delete-tier")]
[JsonDerivedType(typeof(ResizeTierCommand), "resize-tier")]
[JsonDerivedType(typeof(RenameTierCommand), "rename-tier")]
public abstract record RealStorageCommand;

public sealed record SetDiskOnlineCommand(RealTargetReference Disk, bool Online) : RealStorageCommand;
public sealed record InitializeGptCommand(RealTargetReference Disk) : RealStorageCommand;
public sealed record ClearDiskCommand(RealTargetReference Disk, bool RemoveOem) : RealStorageCommand;
public sealed record CreatePartitionCommand(RealTargetReference Disk, RealPartitionRole Role, long OffsetBytes, long SizeBytes) : RealStorageCommand;
public sealed record DeletePartitionCommand(RealTargetReference Partition) : RealStorageCommand;
public sealed record ResizePartitionCommand(RealTargetReference Partition, long SizeBytes) : RealStorageCommand;
public sealed record FormatVolumeCommand(RealTargetReference Partition, RealFileSystem FileSystem, int ClusterBytes, bool Full, string? Label) : RealStorageCommand;
public sealed record SetDriveLetterCommand(RealTargetReference Partition, char? PreviousLetter, char? NewLetter) : RealStorageCommand;
public sealed record RenameVolumeCommand(RealTargetReference Volume, string Label) : RealStorageCommand;
public sealed record CreatePoolCommand(RealTargetReference PhysicalDisk, string Name) : RealStorageCommand;
public sealed record DeletePoolCommand(RealTargetReference Pool) : RealStorageCommand;
public sealed record RenamePoolCommand(RealTargetReference Pool, string Name) : RealStorageCommand;
public sealed record CreateVirtualDiskCommand(RealTargetReference Pool, string Name, long SizeBytes, int InterleaveBytes, int DataColumns) : RealStorageCommand;
public sealed record DeleteVirtualDiskCommand(RealTargetReference VirtualDisk) : RealStorageCommand;
public sealed record ResizeVirtualDiskCommand(RealTargetReference VirtualDisk, long SizeBytes) : RealStorageCommand;
public sealed record RenameVirtualDiskCommand(RealTargetReference VirtualDisk, string Name) : RealStorageCommand;
public sealed record CreateTierCommand(RealTargetReference Pool, string Name, int InterleaveBytes, int DataColumns) : RealStorageCommand;
public sealed record CreateTieredVirtualDiskCommand(RealTargetReference Pool, RealTargetReference Tier, string Name, long SizeBytes) : RealStorageCommand;
public sealed record DeleteTierCommand(RealTargetReference Tier) : RealStorageCommand;
public sealed record ResizeTierCommand(RealTargetReference Tier, long SizeBytes) : RealStorageCommand;
public sealed record RenameTierCommand(RealTargetReference Tier, string Name) : RealStorageCommand;

public sealed record RealOperationStep(
    string Id,
    RealStorageCommand Command,
    IReadOnlyList<string> DependsOn,
    string BeforeCondition,
    string AfterCondition,
    string DataLoss,
    string SupportEvidence);

public sealed record RealOperationSpecification(
    int FormatVersion,
    string AdapterVersion,
    string MachineBinding,
    string SessionBinding,
    string TargetFingerprint,
    string PhysicalMemberFingerprint,
    string SupportEvidence,
    string ExpectedFinalState,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<RealOperationStep> Steps);

// An App proposal. The Agent assigns identity and derives the final plan from fresh facts.
public sealed record RealOperationIntentRequest(
    OperationIntent Intent,
    SystemId SystemId,
    IReadOnlyList<StorageObjectId> Targets,
    IReadOnlyList<RealOperationStep> Steps,
    string ExpectedFinalState);

// The Agent fills this only after checking the OS peer and administrator token.
public sealed record TrustedRealSession(
    SessionId AgentSessionId,
    string ProductSessionId,
    string ProcessInstanceId,
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    string ImagePath,
    bool IsArmed)
{
    public bool IsWellFormed => AgentSessionId.Value != Guid.Empty &&
        !string.IsNullOrWhiteSpace(ProductSessionId) &&
        !string.IsNullOrWhiteSpace(ProcessInstanceId) && ProcessId > 0 &&
        ProcessStartUtc != default && !string.IsNullOrWhiteSpace(ImagePath);

    public string Binding => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new object[] { AgentSessionId.Value, ProductSessionId,
            ProcessInstanceId, ProcessId, ProcessStartUtc.ToUniversalTime(), ImagePath })))).ToLowerInvariant();
}
