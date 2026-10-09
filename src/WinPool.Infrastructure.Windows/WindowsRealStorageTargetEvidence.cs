using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Internal result of a fresh preflight. Numbers are only call parameters after
/// UniqueId/ObjectId, GUID, geometry and the parent association are checked.
/// No UI payload may construct this value for dispatch.
/// </summary>
public sealed record WindowsStorageCommandTarget(
    StorageObjectKind Kind,
    string UniqueId,
    string ObjectId,
    string SerialNumber,
    string OsDiskUniqueId,
    string OsDiskPath,
    int? DiskNumber,
    int? PartitionNumber,
    string PartitionGuid,
    long? OffsetBytes,
    long? SizeBytes,
    string ParentUniqueId,
    string PhysicalMemberUniqueId,
    string StorageSubsystemUniqueId,
    string ExpectedFingerprint,
    string RelatedUniqueId = "",
    string RelatedObjectId = "",
    string PartitionTypeGuid = "",
    bool CreatedInThisPlan = false,
    string StorageSubsystemObjectId = "",
    string PhysicalMemberObjectId = "");

/// <summary>
/// A command return is evidence for post-call capture, not a verified success.
/// The backend must match returned identities to exactly one fresh object and
/// check the planned after-condition before declaring a step verified.
/// </summary>
public sealed record WindowsStorageCommandResult(
    bool ProviderReturned,
    string Code,
    string? UniqueId,
    string? ObjectId,
    string? PartitionGuid,
    int? DiskNumber,
    int? PartitionNumber,
    int? ProviderJobId,
    string? ProviderError,
    WindowsTieredCreationInput? TieredCreationInput = null,
    JsonElement? LiveCapabilityEvidence = null,
    WindowsNativeMsrSafetyEvidence? NativeMsrSafetyEvidence = null,
    IReadOnlyList<WindowsVolumeSafetyEvidence>? VolumeSafetyEvidence = null,
    IReadOnlyList<WindowsNativeMsrSafetyEvidence>? OfflinePartitionAttributes = null,
    WindowsPoolMemberRoleEvidence? PoolMemberRoleEvidence = null);

/// <summary>Frozen creation mechanism and actual provider parameters. Automatic HDD creation records the constraint template separately from the null provider template.</summary>
public sealed record WindowsTieredCreationInput(
    string? TemplateUniqueId,
    string? TemplateObjectId,
    string PoolUniqueId,
    string PhysicalMemberUniqueId,
    string MediaType,
    string ResiliencySettingName,
    string ProvisioningType,
    int NumberOfColumns,
    long Interleave,
    long SizeBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UseMaximumSize = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TieredVirtualDiskCreationMechanism CreationMechanism = TieredVirtualDiskCreationMechanism.ExactTemplate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConstraintTemplateUniqueId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConstraintTemplateObjectId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderTemplateUniqueId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderTemplateObjectId = null);

public interface IWindowsRealStorageCommandAdapter
{
    Task<WindowsStorageCommandResult> ExecuteAsync(
        RealStorageCommand command,
        WindowsStorageCommandTarget target,
        CancellationToken cancellationToken);
}
