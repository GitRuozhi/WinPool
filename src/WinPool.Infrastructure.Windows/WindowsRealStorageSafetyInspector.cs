using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public interface IWindowsRealStorageSafetyInspector
{
    Task ValidateAsync(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken);

    async Task<WindowsRealStorageSafetyEvidence?> ValidateWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        RealStorageCommand command, CancellationToken cancellationToken)
    {
        await ValidateAsync(topology, closure, command, cancellationToken).ConfigureAwait(false);
        return null;
    }

    Task<WindowsRealStorageSafetyEvidence?> ValidateObservedDiskStateWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        SetDiskOnlineCommand command, CancellationToken cancellationToken) =>
        ValidateWithEvidenceAsync(topology, closure, command, cancellationToken);

    Task<WindowsRealStorageSafetyEvidence?> ValidateCreatedPartitionFormatWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        FormatVolumeCommand command, StorageObjectId exactCreatedPartition,
        CancellationToken cancellationToken) =>
        ValidateWithEvidenceAsync(topology, closure, command, cancellationToken);

    Task<WindowsRealStorageSafetyEvidence?> InspectAsync(
        WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<WindowsRealStorageSafetyEvidence?>(
            new NotSupportedException("This safety inspector does not support exact physical member-set MAX."));
}

public sealed record WindowsRealStorageSafetyEvidence(
    WindowsNativeMsrSafetyEvidence? NativePartitionAttributes,
    IReadOnlyList<WindowsVolumeSafetyEvidence> VolumeSafetyEvidence,
    IReadOnlyList<WindowsNativeMsrSafetyEvidence>? OfflinePartitionAttributes = null,
    WindowsPoolMemberRoleEvidence? PoolMemberRoleEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WindowsPoolMemberRoleEvidence>? PhysicalMemberRoleEvidence = null);

public sealed record WindowsVolumeSafetyEvidence(string InventoryVersion, string VolumeStableId,
    string PartitionStableId, string VolumeGuidPath, bool BitLockerEnumerationComplete,
    string BitLockerApplicability, int? VdsFileSystemType, uint? VdsAllocationUnitSize, DateTimeOffset VerifiedAt)
{
    public string VerificationMethod { get; init; } = "VdsExactVolumeFileSystem";
    public WindowsBitLockerDecryptionEvidence? FullyDecryptedBitLocker { get; init; }
}

public sealed record WindowsBitLockerDecryptionEvidence(
    string DeviceId, string ConfirmedDeviceId,
    uint EncryptionMethodReturnValue, uint EncryptionMethod,
    uint ConversionReturnValue, uint ConversionStatus,
    uint LockReturnValue, uint LockStatus)
{
    public string EncryptionState { get; init; } = "FullyDecrypted";
}

public sealed record WindowsNativeMsrSafetyEvidence(
    string InventoryVersion, string PartitionStableId, string DiskPath,
    string DiskUniqueId, string DiskObjectId, Guid DiskGuid,
    string PhysicalUniqueId, string PhysicalObjectId, string SerialNumber,
    Guid PartitionGuid, Guid PartitionType, long Offset, long Length,
    int PartitionNumber, ulong Attributes, DateTimeOffset VerifiedAt)
{
    public bool DiskIsOffline { get; init; }
    public string? VirtualDiskUniqueId { get; init; }
    public string? VirtualDiskObjectId { get; init; }
    public string? PoolUniqueId { get; init; }
    public string? PoolObjectId { get; init; }
    public string NativeBindingMethod { get; init; } = "ProviderPathGptGuidAndPhysicalSerial";
}

/// <summary>
/// Additional local, read-only checks whose absence from a display snapshot
/// must never be interpreted as permission to mutate a disk.
/// </summary>
public sealed class WindowsRealStorageSafetyInspector : IWindowsRealStorageSafetyInspector
{
    public static int InitializeReadOnlyVolumeProbe() =>
        WindowsReadOnlyVdsFileSystemReader.InitializeProcessSecurity();

    internal sealed record BitLockerState(uint? ConversionReturnValue, uint? ConversionStatus,
        uint? LockReturnValue, uint? LockStatus);
    internal sealed record BitLockerVolume(string? DeviceId, string? DriveLetter,
        Func<BitLockerState> ReadState);
    internal sealed record ExactBitLockerState(string? DeviceId, string? ConfirmedDeviceId,
        uint? EncryptionMethodReturnValue, uint? EncryptionMethod, BitLockerState State);
    private static readonly Guid BasicDataRole = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly HashSet<Guid> DeletablePartitionRoles =
    [
        BasicDataRole,
        Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b"),
        Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae"),
        Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac")
    ];
    private readonly string[] additionalCriticalPaths;
    private readonly Action<IReadOnlyList<VolumeInfo>, CancellationToken> encryptionProbe;
    private readonly Func<WindowsNativeMsrIdentity, CancellationToken, WindowsNativeMsrAttributes> msrAttributesReader;

    public WindowsRealStorageSafetyInspector(
        IEnumerable<string>? additionalCriticalPaths = null)
        : this(additionalCriticalPaths, RequireFullyDecryptedVolumes)
    {
    }

    internal WindowsRealStorageSafetyInspector(
        IEnumerable<string>? additionalCriticalPaths,
        Action<IReadOnlyList<VolumeInfo>, CancellationToken> encryptionProbe,
        Func<WindowsNativeMsrIdentity, CancellationToken, WindowsNativeMsrAttributes>? msrAttributesReader = null)
    {
        this.additionalCriticalPaths = additionalCriticalPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).ToArray() ?? [];
        this.encryptionProbe = encryptionProbe ?? throw new ArgumentNullException(nameof(encryptionProbe));
        this.msrAttributesReader = msrAttributesReader ?? WindowsNativeMsrAttributesReader.Read;
    }

    public Task ValidateAsync(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken) => ValidateWithEvidenceAsync(
            topology, closure, command, cancellationToken);

    public Task<WindowsRealStorageSafetyEvidence?> ValidateWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        RealStorageCommand command, CancellationToken cancellationToken) => Task.Run(() =>
            Validate(topology, closure, command, cancellationToken), cancellationToken);

    public Task<WindowsRealStorageSafetyEvidence?> ValidateObservedDiskStateWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        SetDiskOnlineCommand command, CancellationToken cancellationToken) => Task.Run(() =>
            Validate(topology, closure, command, cancellationToken, observedDiskState: true), cancellationToken);

    public Task<WindowsRealStorageSafetyEvidence?> ValidateCreatedPartitionFormatWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        FormatVolumeCommand command, StorageObjectId exactCreatedPartition,
        CancellationToken cancellationToken) => Task.Run(() =>
            Validate(topology, closure, command, cancellationToken,
                exactCreatedPartition: exactCreatedPartition), cancellationToken);

    public Task<WindowsRealStorageSafetyEvidence?> InspectAsync(
        WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken) => Task.Run<WindowsRealStorageSafetyEvidence?>(() =>
            InspectExactPhysicalMemberSet(topology, closure, command, cancellationToken), cancellationToken);

    private WindowsRealStorageSafetyEvidence InspectExactPhysicalMemberSet(
        WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure supplied,
        RealStorageCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (supplied is null || supplied.InventoryVersion != topology.InventoryVersion
            || supplied.Members is null || supplied.Members.Count < 2
            || supplied.Members.Any(item => item is null || item.PoolMemberRoleEvidence is null))
            throw new InvalidDataException("Multi-tier MAX needs a fresh exact physical member-set closure.");

        var approvedMembers = supplied.Members
            .Select(item => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, item.PhysicalDiskId))
            .ToArray();
        var current = topology.RequireExactPhysicalMemberSet(supplied.ConcretePool, approvedMembers);
        if (!SameExactPhysicalMemberSet(supplied, current))
            throw new InvalidDataException("The exact physical member-set closure changed or was modified after capture.");

        var tierTarget = command switch
        {
            CreateTieredVirtualDiskCommand { Pool.Existing: { } poolId, Tier.Existing: { } tierId }
                when poolId == current.ConcretePool && tierId.Kind == StorageObjectKind.StorageTier => tierId,
            ResizeTierCommand { Tier.Existing: { } tierId }
                when tierId.Kind == StorageObjectKind.StorageTier => tierId,
            _ => throw new InvalidDataException("Exact physical member-set inspection is limited to existing-pool tiered MAX creation and growth.")
        };
        if (tierTarget.System != topology.SystemId
            || current.Objects.All(item => item.Id != tierTarget.ProviderKey
                || item.ObjectType != FactObjectType.StorageTier))
            throw new InvalidDataException("The MAX tier input is outside the exact approved pool closure.");

        foreach (var member in current.Members)
            ValidateExactPhysicalMemberRoleAndHealth(topology, current.PoolId, member);

        // The ordinary validator already checks the entire connected OS-disk,
        // partition, volume, BitLocker, runtime-path and command safety surface.
        // Run it once for the full component, then return the role proofs for
        // every member in the versioned set.
        var firstMember = current.Members[0];
        var componentClosure = new RealTargetClosure(
            firstMember.PhysicalDiskId,
            current.Objects,
            current.Fingerprint,
            current.PhysicalMemberFingerprint)
        {
            PoolMemberRoleEvidence = firstMember.PoolMemberRoleEvidence
        };
        var evidence = Validate(topology, componentClosure, command, cancellationToken)
            ?? throw new InvalidDataException("The exact member-set safety inspection returned no evidence.");
        return evidence with
        {
            PoolMemberRoleEvidence = null,
            PhysicalMemberRoleEvidence = current.Members
                .Select(item => item.PoolMemberRoleEvidence)
                .ToArray()
        };
    }

    private static bool SameExactPhysicalMemberSet(
        RealExactPhysicalMemberSetClosure supplied,
        RealExactPhysicalMemberSetClosure current)
    {
        if (supplied.ConcretePool != current.ConcretePool
            || supplied.PoolId != current.PoolId
            || supplied.PoolUniqueId != current.PoolUniqueId
            || supplied.PoolObjectId != current.PoolObjectId
            || supplied.InventoryVersion != current.InventoryVersion
            || supplied.Fingerprint != current.Fingerprint
            || supplied.PhysicalMemberFingerprint != current.PhysicalMemberFingerprint
            || supplied.Members.Count != current.Members.Count)
            return false;

        for (var index = 0; index < current.Members.Count; index++)
        {
            var left = supplied.Members[index];
            var right = current.Members[index];
            if (left.PhysicalDiskId != right.PhysicalDiskId
                || left.UniqueId != right.UniqueId
                || left.ObjectId != right.ObjectId
                || left.SerialNumber != right.SerialNumber
                || left.SizeBytes != right.SizeBytes
                || left.PoolMemberRoleEvidence is null
                || !SamePoolMemberRoleEvidence(left.PoolMemberRoleEvidence, right.PoolMemberRoleEvidence))
                return false;
        }

        return true;
    }

    private static bool SamePoolMemberRoleEvidence(
        WindowsPoolMemberRoleEvidence left,
        WindowsPoolMemberRoleEvidence right) =>
        left.InventoryVersion == right.InventoryVersion
        && left.PhysicalStableId == right.PhysicalStableId
        && left.PoolStableId == right.PoolStableId
        && left.IsBoot == right.IsBoot
        && left.IsSystem == right.IsSystem
        && left.IsPageFile == right.IsPageFile
        && left.IsCrashDump == right.IsCrashDump
        && left.VerificationMethod == right.VerificationMethod
        && left.AssociatedOsDiskIds.Order(StringComparer.Ordinal)
            .SequenceEqual(right.AssociatedOsDiskIds.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static void ValidateExactPhysicalMemberRoleAndHealth(
        WindowsRealStorageTopology topology,
        string poolId,
        WindowsExactPhysicalMemberRoleEvidence member)
    {
        var physical = topology.Snapshot.PhysicalDisks.SingleOrDefault(item => item.StableId == member.PhysicalDiskId);
        var roles = member.PoolMemberRoleEvidence;
        if (physical is null || physical.PoolStableId != poolId
            || physical.IsBoot || physical.IsSystem || physical.IsPageFile || physical.IsCrashDump
            || physical.IsRetired || physical.IsHotSpare
            || !physical.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
            || roles.InventoryVersion != topology.InventoryVersion
            || roles.PhysicalStableId != physical.StableId
            || roles.PoolStableId != poolId
            || roles.IsBoot || roles.IsSystem || roles.IsPageFile || roles.IsCrashDump)
            throw new InvalidDataException("An exact pool member has a protected Windows role or unsafe health state.");

        if (topology.Snapshot.FieldIssues.Any(issue => issue.ObjectId == physical.StableId
            && issue.State != FieldReadState.Returned
            && issue.FieldName is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump")
            && (roles.InventoryVersion != topology.InventoryVersion
                || roles.PhysicalStableId != physical.StableId
                || roles.PoolStableId != poolId))
            throw new InvalidDataException("An exact pool member's unknown role lacks current aggregate evidence.");
    }

    private WindowsRealStorageSafetyEvidence? Validate(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken,
        bool observedDiskState = false,
        StorageObjectId? exactCreatedPartition = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (exactCreatedPartition is not null && command is not FormatVolumeCommand
            { Partition.Kind: StorageObjectKind.Partition, Partition.Existing: null, Partition.CreatedByStep: { Length: > 0 },
                FileSystem: RealFileSystem.Fat32, ClusterBytes: 4096, Full: false })
            throw new InvalidDataException("Native EFI format evidence requires a newly created FAT32/4096 quick-format target.");
        var snapshot = topology.Snapshot;
        var physical = snapshot.PhysicalDisks.Single(item =>
            item.StableId == closure.PhysicalDiskId);
        if (snapshot.FieldIssues.Any(issue => issue.ObjectId == physical.StableId
            && issue.State != FieldReadState.Returned
            && issue.FieldName is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump"))
        {
            if (closure.PoolMemberRoleEvidence is not { } roles
                || roles.InventoryVersion != topology.InventoryVersion || roles.PhysicalStableId != physical.StableId
                || roles.PoolStableId != physical.PoolStableId
                || roles.IsBoot || roles.IsSystem || roles.IsPageFile || roles.IsCrashDump)
                throw new InvalidDataException("Unknown physical roles lack a fresh exact pool and OS-disk aggregate proof.");
        }
        if (physical.IsBoot || physical.IsSystem || physical.IsPageFile
            || physical.IsCrashDump || physical.IsRetired || physical.IsHotSpare
            || !physical.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The physical member has a protected role or unsafe health state.");

        var diskIds = closure.Objects.Where(item => item.ObjectType == FactObjectType.Disk)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        string? offlineMetadataDiskId = null;
        if (command is SetDiskOnlineCommand { Disk.Existing: { } onlineTarget,
                Disk.CreatedByStep: null }
            && onlineTarget.System == topology.SystemId && onlineTarget.Kind == StorageObjectKind.OsDisk
            && diskIds.Contains(onlineTarget.ProviderKey)
            && snapshot.OsDisks.SingleOrDefault(item => item.StableId == onlineTarget.ProviderKey) is { IsOffline: true }
            && (command is SetDiskOnlineCommand { Online: true } || observedDiskState))
            offlineMetadataDiskId = onlineTarget.ProviderKey;
        if (observedDiskState)
        {
            if (command is not SetDiskOnlineCommand { Disk.Existing: { } observedTarget, Disk.CreatedByStep: null } observed
                || observedTarget.System != topology.SystemId || observedTarget.Kind != StorageObjectKind.OsDisk
                || !diskIds.Contains(observedTarget.ProviderKey)
                || snapshot.OsDisks.SingleOrDefault(item => item.StableId == observedTarget.ProviderKey) is not { } observedDisk
                || observedDisk.IsOffline != !observed.Online)
                throw new InvalidDataException("Read-only disk-state verification requires the exact requested current state.");
        }
        foreach (var disk in snapshot.OsDisks.Where(item => diskIds.Contains(item.StableId)))
        {
            var raw = topology.RequireObject(new StorageObjectId(
                topology.SystemId, StorageObjectKind.OsDisk, disk.StableId));
            if (disk.IsBoot || disk.IsSystem || RequiredBoolean(raw, "IsClustered")
                || RequiredBoolean(raw, "IsReadOnly"))
                throw new InvalidDataException("The OS disk is clustered, read-only, or protected by Windows.");
            if (disk.IsOffline && disk.StableId != offlineMetadataDiskId)
                throw new InvalidDataException("An offline disk cannot receive this storage command.");
            if (disk.PartitionStyle is not ("RAW" or "GPT" or "MBR"))
                throw new InvalidDataException("The disk layout is outside the supported basic-disk scope.");
        }

        var partitionIds = closure.Objects.Where(item => item.ObjectType == FactObjectType.Partition)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var deletingPartition = command is DeletePartitionCommand;
        string[] selectedAccessPaths = [];
        string? nativeVerifiedPartition = null;
        WindowsNativeMsrSafetyEvidence? nativeEvidence = null;
        var offlineEvidence = new List<WindowsNativeMsrSafetyEvidence>();
        if (command is DeletePartitionCommand || exactCreatedPartition is not null)
        {
            var selectedTarget = command is DeletePartitionCommand delete ? delete.Partition.Existing : exactCreatedPartition;
            if (selectedTarget is not { } target
                || target.System != topology.SystemId
                || target.Kind != StorageObjectKind.Partition
                || command is DeletePartitionCommand { Partition.CreatedByStep: not null }
                || !partitionIds.Contains(target.ProviderKey))
                throw new InvalidDataException("Selected partition safety needs one exact target in the current physical closure.");
            var selected = snapshot.Partitions.SingleOrDefault(item => item.StableId == target.ProviderKey)
                ?? throw new InvalidDataException("The selected partition is absent from the current snapshot.");
            if (selected.IsBoot || selected.IsSystem
                || !Guid.TryParse(selected.PartitionTypeId, out var role)
                || !DeletablePartitionRoles.Contains(role)
                || exactCreatedPartition is not null && role != WindowsNativeMsrAttributesReader.EfiRole
                || role == BasicDataRole && selected.IsHidden)
                throw new InvalidDataException("The selected partition has an unsupported or protected role.");
            var source = topology.RequireObject(target);
            var paths = source.Field("AccessPaths");
            if (WindowsNativeMsrAttributesReader.SupportsRole(role)
                && (role != WindowsNativeMsrAttributesReader.MsrRole
                    || ReturnedNull(paths)
                    || ReturnedNull(source.Field("IsReadOnly"))
                    || ReturnedNull(source.Field("IsShadowCopy"))))
            {
                // Returned null is not false. Independently read the exact GPT
                // attributes while retaining every role, mount, and runtime guard.
                if (!ReturnedEmpty(source.Field("DriveLetter"))
                    || !string.IsNullOrEmpty(selected.DriveLetter)
                    || RequiredBoolean(source, "IsBoot") || RequiredBoolean(source, "IsSystem")
                    || RequiredBoolean(source, "IsOffline")
                    || !FalseOrReturnedNull(source.Field("IsReadOnly"))
                    || !FalseOrReturnedNull(source.Field("IsShadowCopy")))
                    throw new InvalidDataException("Provider-null GPT safety fields lack an exact nonprotected target.");
                if (role == WindowsNativeMsrAttributesReader.MsrRole)
                {
                    if (!string.IsNullOrEmpty(selected.Path) || !string.IsNullOrEmpty(selected.FileSystem)
                        || snapshot.Volumes.Any(item => item.PartitionStableId == selected.StableId)
                        || !(ReturnedNull(paths) || paths is { ReadState: FieldReadState.Returned,
                            Value: { ValueKind: JsonValueKind.Array } empty } && empty.GetArrayLength() == 0))
                        throw new InvalidDataException("Provider-null MSR safety fields lack an exact unmounted, unformatted target.");
                }
                else
                {
                    var related = snapshot.Volumes.Where(item => item.PartitionStableId == selected.StableId).ToArray();
                    var expectedFs = role == WindowsNativeMsrAttributesReader.EfiRole ? "FAT32" : "NTFS";
                    var unformattedRole = (role == WindowsNativeMsrAttributesReader.EfiRole
                            || role == WindowsNativeMsrAttributesReader.RecoveryRole)
                        && IsUnformatted(selected.FileSystem, selected.AllocationUnitSize)
                        && related.Length == 1 && IsUnformatted(related[0].FileSystem, related[0].AllocationUnitSize);
                    var formattedRole = related.Length == 1
                        && selected.FileSystem.Equals(expectedFs, StringComparison.OrdinalIgnoreCase)
                        && related[0].FileSystem.Equals(expectedFs, StringComparison.OrdinalIgnoreCase)
                        && selected.AllocationUnitSize == 4096 && related[0].AllocationUnitSize == 4096;
                    // VolumeInfo can inherit its parent's AccessPaths. Comparing
                    // those two projected lists alone does not bind the volume.
                    var volumePath = related.Length == 1 ? topology.RequireObject(new StorageObjectId(
                        topology.SystemId, StorageObjectKind.Volume, related[0].StableId)).Field("Path") : null;
                    if (paths is not { ReadState: FieldReadState.Returned,
                            Value: { ValueKind: JsonValueKind.Array } knownPaths }
                        || knownPaths.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                        || related.Length != 1 || !(unformattedRole || formattedRole)
                        || !string.IsNullOrEmpty(related[0].DriveLetter)
                        || !knownPaths.EnumerateArray().Select(item => item.GetString()!)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(related[0].AccessPaths)
                        || related[0].AccessPaths.Count(IsVolumeGuidPath) != 1
                        || volumePath is not { ReadState: FieldReadState.Returned,
                            Value: { ValueKind: JsonValueKind.String } returnedVolumePath }
                        || !IsVolumeGuidPath(returnedVolumePath.GetString()!)
                        || !StringComparer.OrdinalIgnoreCase.Equals(returnedVolumePath.GetString(),
                            related[0].AccessPaths.Single(IsVolumeGuidPath)))
                        throw new InvalidDataException("EFI/Recovery safety requires its exact supported volume and known unassigned access paths.");
                }
                var expected = WindowsNativeMsrAttributesReader.Identify(topology, selected);
                var evidence = msrAttributesReader(expected, cancellationToken);
                if (evidence.Identity != expected
                    || (evidence.Attributes & (WindowsNativeMsrAttributes.PlatformRequired | WindowsNativeMsrAttributes.ReadOnly
                        | WindowsNativeMsrAttributes.ShadowCopy)) != 0)
                    throw new InvalidDataException("Native selected MSR attributes are protected or do not match its exact identity.");
                nativeVerifiedPartition = selected.StableId;
                nativeEvidence = new(topology.InventoryVersion, selected.StableId,
                    expected.DiskPath, expected.DiskUniqueId, expected.DiskObjectId,
                    expected.DiskGuid, expected.PhysicalUniqueId, expected.PhysicalObjectId,
                    expected.SerialNumber, expected.PartitionGuid, expected.PartitionType,
                    expected.Offset, expected.Length, expected.PartitionNumber,
                    evidence.Attributes, DateTimeOffset.UtcNow)
                {
                    VirtualDiskUniqueId = expected.VirtualDiskUniqueId,
                    VirtualDiskObjectId = expected.VirtualDiskObjectId,
                    PoolUniqueId = expected.PoolUniqueId,
                    PoolObjectId = expected.PoolObjectId,
                    NativeBindingMethod = expected.NativeBindingMethod
                };
            }
            if (nativeVerifiedPartition == selected.StableId && role == WindowsNativeMsrAttributesReader.MsrRole && ReturnedNull(paths))
                selectedAccessPaths = [];
            else if (paths is not { ReadState: FieldReadState.Returned,
                    Value: { ValueKind: JsonValueKind.Array } value }
                || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("The selected partition's access paths are unknown.");
            else selectedAccessPaths = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (deletingPartition)
                partitionIds = new HashSet<string>(StringComparer.Ordinal) { selected.StableId };
        }
        foreach (var partition in snapshot.Partitions.Where(item =>
                     partitionIds.Contains(item.StableId)))
        {
            var raw = topology.RequireObject(new StorageObjectId(
                topology.SystemId, StorageObjectKind.Partition, partition.StableId));
            var nonData = Guid.TryParse(partition.PartitionTypeId, out var role)
                && (role == Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae")
                    || role == Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")
                    || role == Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac"));
            var provenPartition = partition.StableId == nativeVerifiedPartition;
            var offlineContext = offlineMetadataDiskId is not null && partition.OsDiskStableId == offlineMetadataDiskId;
            if (offlineContext)
            {
                if (partition.IsBoot || partition.IsSystem || RequiredBoolean(raw, "IsBoot") || RequiredBoolean(raw, "IsSystem")
                    || !FalseOrReturnedNull(raw.Field("IsReadOnly"))
                    || !FalseOrReturnedNull(raw.Field("IsShadowCopy"))
                    || !(ReturnedNull(raw.Field("IsOffline"))
                        || raw.Field("IsOffline") is { ReadState: FieldReadState.Returned,
                            Value: { ValueKind: JsonValueKind.True or JsonValueKind.False } })
                    || !(ReturnedNull(raw.Field("IsHidden"))
                        || raw.Field("IsHidden") is { ReadState: FieldReadState.Returned,
                            Value: { ValueKind: JsonValueKind.True or JsonValueKind.False } })
                    || !ReturnedEmpty(raw.Field("DriveLetter")))
                    throw new InvalidDataException("The exact offline partition has unknown or protected metadata.");
                var expected = WindowsNativeMsrAttributesReader.Identify(topology, partition, expectedDiskOffline: true);
                var attributes = msrAttributesReader(expected, cancellationToken);
                if (attributes.Identity != expected
                    || (attributes.Attributes & (WindowsNativeMsrAttributes.PlatformRequired
                        | WindowsNativeMsrAttributes.ReadOnly | WindowsNativeMsrAttributes.ShadowCopy)) != 0)
                    throw new InvalidDataException("The exact offline partition has protected or mismatched native GPT attributes.");
                offlineEvidence.Add(new(topology.InventoryVersion, partition.StableId,
                    expected.DiskPath, expected.DiskUniqueId, expected.DiskObjectId,
                    expected.DiskGuid, expected.PhysicalUniqueId, expected.PhysicalObjectId,
                    expected.SerialNumber, expected.PartitionGuid, expected.PartitionType,
                    expected.Offset, expected.Length, expected.PartitionNumber, attributes.Attributes, DateTimeOffset.UtcNow)
                {
                    DiskIsOffline = true,
                    VirtualDiskUniqueId = expected.VirtualDiskUniqueId,
                    VirtualDiskObjectId = expected.VirtualDiskObjectId,
                    PoolUniqueId = expected.PoolUniqueId,
                    PoolObjectId = expected.PoolObjectId,
                    NativeBindingMethod = expected.NativeBindingMethod
                });
            }
            if (RequiredBoolean(raw, "IsReadOnly", nonData && !deletingPartition || provenPartition || offlineContext)
                || RequiredBoolean(raw, "IsOffline", offlineContext) && !offlineContext
                || RequiredBoolean(raw, "IsShadowCopy", nonData && !deletingPartition || provenPartition || offlineContext))
                throw new InvalidDataException("A related partition is read-only, offline, or a shadow copy.");
            if (partition.PartitionTypeId is { Length: > 0 }
                && Guid.TryParse(partition.PartitionTypeId, out var type)
                && (type == Guid.Parse("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3")
                    || type == Guid.Parse("af9b60a0-1431-4f62-bc68-3311714a69ad")))
                throw new InvalidDataException("Dynamic-disk LDM partitions are outside this stage.");
        }

        if (closure.OfflinePartitionIdsNeedingNativeProof.Any(id =>
            !offlineEvidence.Any(evidence => evidence.PartitionStableId == id)))
            throw new InvalidDataException("An offline partition's unavailable projected role lacks exact native evidence.");

        var volumes = snapshot.Volumes.Where(item =>
            item.PartitionStableId is not null
            && partitionIds.Contains(item.PartitionStableId)
            // Restoring the exact offline disk cannot query its dismounted volumes.
            // This exception only changes online state; every data mutation probes
            // encryption again after the fresh online inventory.
            && !snapshot.Partitions.Any(partition => partition.StableId == item.PartitionStableId
                && offlineMetadataDiskId is not null && partition.OsDiskStableId == offlineMetadataDiskId))
            .ToArray();
        IReadOnlyList<WindowsVolumeSafetyEvidence> volumeEvidence = [];
        if (encryptionProbe == RequireFullyDecryptedVolumes)
        {
            foreach (var volume in volumes) RequireExactVolumeGuidPath(topology, volume);
            volumeEvidence = RequireFullyDecryptedVolumes(volumes, cancellationToken, snapshot.Partitions
                .Where(item => Guid.TryParse(item.PartitionTypeId, out var role)
                    && role == WindowsNativeMsrAttributesReader.EfiRole
                    && nativeVerifiedPartition == item.StableId)
                .Select(item => item.StableId).ToHashSet(StringComparer.Ordinal))
                .Select(item => item with { InventoryVersion = topology.InventoryVersion }).ToArray();
        }
        else encryptionProbe(volumes, cancellationToken);

        if (MayInterruptRuntime(command))
            RequireRuntimeIndependent(volumes, selectedAccessPaths);
        return new(nativeEvidence, volumeEvidence, offlineEvidence, closure.PoolMemberRoleEvidence);
    }

    private static bool RequiredBoolean(WinPoolSourceObject item, string name,
        bool allowNotApplicableNull = false)
    {
        var field = item.Field(name);
        if (allowNotApplicableNull && ReturnedNull(field))
            return false;
        if (field is not { ReadState: FieldReadState.Returned,
            Value: { } value }
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("A required Windows safety field is unavailable: " + name);
        return value.ValueKind == JsonValueKind.True;
    }

    private static bool ReturnedNull(WinPoolSourceField? field) =>
        field is { ReadState: FieldReadState.Returned }
        && (field.Value is null || field.Value.Value.ValueKind == JsonValueKind.Null);
    private static bool ReturnedEmpty(WinPoolSourceField? field) => ReturnedNull(field)
        || field is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } value }
        && string.IsNullOrEmpty(value.GetString());
    private static bool FalseOrReturnedNull(WinPoolSourceField? field) => ReturnedNull(field)
        || field is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.False } };
    private static bool IsUnformatted(string fileSystem, long? allocationUnitSize) =>
        (string.IsNullOrWhiteSpace(fileSystem) || fileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase))
        && allocationUnitSize == 0;

    internal static string RequireExactVolumeGuidPath(WindowsRealStorageTopology topology, VolumeInfo volume)
    {
        var source = topology.RequireObject(new StorageObjectId(topology.SystemId,
            StorageObjectKind.Volume, volume.StableId));
        var paths = volume.AccessPaths.Where(IsVolumeGuidPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != 1 || source.Field("Path") is not
            { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } path }
            || !IsVolumeGuidPath(path.GetString()!)
            || !SameVolumeGuidPath(path.GetString(), paths[0]))
            throw new InvalidDataException("The volume GUID access path does not match its independent Windows volume identity.");
        return paths[0];
    }

    private static void RequireFullyDecryptedVolumes(
        IReadOnlyList<VolumeInfo> volumes,
        CancellationToken cancellationToken) => RequireFullyDecryptedVolumes(volumes, cancellationToken,
            new HashSet<string>(StringComparer.Ordinal));

    private static IReadOnlyList<WindowsVolumeSafetyEvidence> RequireFullyDecryptedVolumes(
        IReadOnlyList<VolumeInfo> volumes, CancellationToken cancellationToken,
        IReadOnlySet<string> efiPartitions)
    {
        if (volumes.Count == 0) return [];
        using var searcher = new ManagementObjectSearcher(
            @"root\CIMV2\Security\MicrosoftVolumeEncryption",
            "SELECT DeviceID,DriveLetter FROM Win32_EncryptableVolume");
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var candidates = results.Cast<ManagementObject>().ToArray();
        var evidence = new List<WindowsVolumeSafetyEvidence>();
        try
        {
            foreach (var volume in volumes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var probes = candidates.Select(instance => new BitLockerVolume(
                    instance["DeviceID"]?.ToString()?.Trim(), instance["DriveLetter"]?.ToString()?.Trim(), () =>
                    ReadBitLockerState(instance))).ToArray();
                var proof = RequireBitLockerSafety(volume, probes, WindowsReadOnlyVdsFileSystemReader.Read,
                    volume.PartitionStableId is { } partitionId && efiPartitions.Contains(partitionId), cancellationToken,
                    ReadExactBitLockerVolume);
                if (proof is not null) evidence.Add(proof);
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
        return evidence;
    }

    private static BitLockerState ReadBitLockerState(ManagementObject instance, InvokeMethodOptions? options = null)
    {
        using var conversionParameters = instance.GetMethodParameters("GetConversionStatus");
        conversionParameters["PrecisionFactor"] = 0U;
        using var conversion = instance.InvokeMethod("GetConversionStatus", conversionParameters, options);
        using var lockStatus = instance.InvokeMethod("GetLockStatus", null, options);
        return new(conversion?["ReturnValue"] as uint?, conversion?["ConversionStatus"] as uint?,
            lockStatus?["ReturnValue"] as uint?, lockStatus?["LockStatus"] as uint?);
    }

    private static ExactBitLockerState? ReadExactBitLockerVolume(string volumePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsVolumeGuidPath(volumePath))
            throw new InvalidDataException("The direct BitLocker probe requires one exact volume GUID path.");
        var scope = new ManagementScope(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
            new ConnectionOptions { Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate, Timeout = TimeSpan.FromSeconds(10) });
        var escapedPath = volumePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        using var instance = new ManagementObject(scope,
            new ManagementPath("Win32_EncryptableVolume.DeviceID=\"" + escapedPath + "\""),
            new ObjectGetOptions { Timeout = TimeSpan.FromSeconds(10) });
        try
        {
            instance.Get();
        }
        catch (ManagementException exception) when (exception.ErrorCode == ManagementStatus.NotFound)
        {
            // No object is not an encryption result. The caller may still use
            // an independently successful, existing VDS or native EFI proof.
            return null;
        }
        var deviceId = instance["DeviceID"] as string;
        if (!SameVolumeGuidPath(deviceId, volumePath))
            throw new InvalidDataException("The direct BitLocker object has a different volume identity.");
        token.ThrowIfCancellationRequested();
        var options = new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) };
        using var encryption = instance.InvokeMethod("GetEncryptionMethod", null, options);
        var state = ReadBitLockerState(instance, options);
        token.ThrowIfCancellationRequested();
        instance.Get();
        return new(deviceId, instance["DeviceID"] as string,
            encryption?["ReturnValue"] as uint?, encryption?["EncryptionMethod"] as uint?, state);
    }

    internal static WindowsVolumeSafetyEvidence? RequireBitLockerSafety(VolumeInfo volume, IReadOnlyList<BitLockerVolume> candidates,
        Func<string, CancellationToken, WindowsReadOnlyVdsFileSystemReader.Evidence> fileSystemReader,
        bool isEfiPartition, CancellationToken token,
        Func<string, CancellationToken, ExactBitLockerState?>? exactVolumeReader = null)
    {
        token.ThrowIfCancellationRequested();
        var matches = candidates.Where(item => MatchesVolume(item, volume)).ToArray();
        if (matches.Length == 1)
        {
            var state = matches[0].ReadState();
            if (state.ConversionReturnValue != 0 || state.LockReturnValue != 0
                || state.ConversionStatus != 0 || state.LockStatus != 0)
                throw new InvalidDataException("A related volume is encrypted, locked, or has unknown BitLocker state.");
            return null;
        }
        if (matches.Length != 0)
            throw new InvalidDataException("BitLocker state cannot be uniquely mapped to a related volume.");

        var paths = volume.AccessPaths.Where(IsVolumeGuidPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != 1)
            throw new InvalidDataException("The related volume lacks one exact GUID for encryption verification.");
        if (exactVolumeReader?.Invoke(paths[0], token) is { } direct)
        {
            if (!SameVolumeGuidPath(direct.DeviceId, paths[0])
                || !SameVolumeGuidPath(direct.ConfirmedDeviceId, paths[0]))
                throw new InvalidDataException("The direct BitLocker probe returned a different volume identity.");
            if (direct.EncryptionMethodReturnValue != 0 || direct.EncryptionMethod != 0
                || direct.State.ConversionReturnValue != 0 || direct.State.ConversionStatus != 0
                || direct.State.LockReturnValue != 0 || direct.State.LockStatus != 0)
                throw new InvalidDataException("The exact directly queried volume is encrypted, locked, or has unknown BitLocker state.");
            return new("", volume.StableId,
                volume.PartitionStableId ?? throw new InvalidDataException("The exact volume lacks a partition association."),
                paths[0], true, "Applicable", null, null, DateTimeOffset.UtcNow)
            {
                VerificationMethod = "Win32EncryptableVolumeExactGet",
                FullyDecryptedBitLocker = new(direct.DeviceId!, direct.ConfirmedDeviceId!,
                    direct.EncryptionMethodReturnValue.GetValueOrDefault(), direct.EncryptionMethod.GetValueOrDefault(),
                    direct.State.ConversionReturnValue.GetValueOrDefault(), direct.State.ConversionStatus.GetValueOrDefault(),
                    direct.State.LockReturnValue.GetValueOrDefault(), direct.State.LockStatus.GetValueOrDefault())
            };
        }

        // Missing from a fully enumerated encryptable-volume provider is only an
        // applicability candidate. Independently classify the exact GUID volume.
        // RAW/UNKNOWN display text alone must never exempt a locked volume.
        var raw = IsUnformatted(volume.FileSystem, volume.AllocationUnitSize);
        if (isEfiPartition && paths.Length == 1 && string.IsNullOrEmpty(volume.DriveLetter)
            && (raw || volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && volume.AllocationUnitSize == 4096))
        {
            // The caller has freshly proved this exact GPT EFI System Partition
            // and its unprotected native attributes. Microsoft excludes the
            // system-partition designation from BitLocker applicability:
            // https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/faq
            // Complete enumeration and no exact match are required above. This
            // records applicability, without claiming VDS or decryption success.
            return new("", volume.StableId,
                volume.PartitionStableId ?? throw new InvalidDataException("The exact EFI volume lacks a partition association."),
                paths[0], true, "NotApplicableNativeEfiSystemPartition", null, null, DateTimeOffset.UtcNow)
            { VerificationMethod = "NativeGptEfiSystemPartition" };
        }
        if (isEfiPartition)
            throw new InvalidDataException("The native EFI volume's exact filesystem or unassigned access paths are unavailable.");
        if (!raw || paths.Length != 1)
            throw new InvalidDataException("BitLocker state cannot be uniquely mapped to a related volume.");
        var evidence = fileSystemReader(paths[0], token);
        if (!StringComparer.OrdinalIgnoreCase.Equals(evidence.VolumePath, paths[0])
            || evidence.FileSystemType != 1)
            throw new InvalidDataException("The exact volume is encrypted or its filesystem applicability is unknown.");
        return new("", volume.StableId,
            volume.PartitionStableId ?? throw new InvalidDataException("The exact volume lacks a partition association."),
            paths[0], true, "NotApplicableUnformatted",
            evidence.FileSystemType, evidence.AllocationUnitSize, DateTimeOffset.UtcNow);
    }

    internal static bool IsVolumeGuidPath(string path) => path.Length == 49
        && path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith("}\\", StringComparison.Ordinal)
        && Guid.TryParse(path.AsSpan(11, 36), out var guid) && guid != Guid.Empty;

    private static bool SameVolumeGuidPath(string? candidate, string expected) =>
        candidate is not null && StringComparer.OrdinalIgnoreCase.Equals(
            candidate.TrimEnd('\\'), expected.TrimEnd('\\'));

    private static bool MatchesVolume(BitLockerVolume item, VolumeInfo volume)
    {
        var deviceId = item.DeviceId;
        var paths = volume.AccessPaths.Where(IsVolumeGuidPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length == 1 && !string.IsNullOrWhiteSpace(deviceId)
            && StringComparer.OrdinalIgnoreCase.Equals(paths[0].TrimEnd('\\'), deviceId.TrimEnd('\\'));
    }

    private static bool MayInterruptRuntime(RealStorageCommand command) => command is
        ClearDiskCommand or DeletePartitionCommand or FormatVolumeCommand
        or ResizePartitionCommand or DeleteVirtualDiskCommand or DeletePoolCommand
        or SetDiskOnlineCommand { Online: false }
        or SetDriveLetterCommand { PreviousLetter: not null };

    private void RequireRuntimeIndependent(IReadOnlyList<VolumeInfo> volumes,
        IReadOnlyList<string> selectedAccessPaths)
    {
        var critical = new[]
        {
            Environment.ProcessPath,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "WinPool"),
            Path.GetTempPath()
        }.Concat(additionalCriticalPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!)).ToArray();
        foreach (var accessPath in volumes.SelectMany(volume => volume.AccessPaths)
                     .Concat(selectedAccessPaths))
        {
            if (!Path.IsPathFullyQualified(accessPath)) continue;
            var normalized = Path.GetFullPath(accessPath);
            var prefix = normalized.EndsWith('\\') ? normalized : normalized + '\\';
            var active = critical.FirstOrDefault(path =>
                StringComparer.OrdinalIgnoreCase.Equals(path, normalized)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (active is not null)
                throw new InvalidDataException("The storage target contains an active WinPool path: "
                    + active + " on " + normalized);
        }
    }
}
