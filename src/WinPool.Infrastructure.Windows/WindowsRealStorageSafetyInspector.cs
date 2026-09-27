using System.Management;
using System.Text.Json;
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
}

/// <summary>
/// Additional local, read-only checks whose absence from a display snapshot
/// must never be interpreted as permission to mutate a disk.
/// </summary>
public sealed class WindowsRealStorageSafetyInspector : IWindowsRealStorageSafetyInspector
{
    private static readonly Guid BasicDataRole = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private readonly string[] additionalCriticalPaths;
    private readonly Action<IReadOnlyList<VolumeInfo>, CancellationToken> encryptionProbe;

    public WindowsRealStorageSafetyInspector(
        IEnumerable<string>? additionalCriticalPaths = null)
        : this(additionalCriticalPaths, RequireFullyDecryptedVolumes)
    {
    }

    internal WindowsRealStorageSafetyInspector(
        IEnumerable<string>? additionalCriticalPaths,
        Action<IReadOnlyList<VolumeInfo>, CancellationToken> encryptionProbe)
    {
        this.additionalCriticalPaths = additionalCriticalPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).ToArray() ?? [];
        this.encryptionProbe = encryptionProbe ?? throw new ArgumentNullException(nameof(encryptionProbe));
    }

    public Task ValidateAsync(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken) => Task.Run(() =>
            Validate(topology, closure, command, cancellationToken), cancellationToken);

    private void Validate(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealStorageCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = topology.Snapshot;
        var physical = snapshot.PhysicalDisks.Single(item =>
            item.StableId == closure.PhysicalDiskId);
        if (physical.IsBoot || physical.IsSystem || physical.IsPageFile
            || physical.IsCrashDump || physical.IsRetired || physical.IsHotSpare
            || !physical.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The physical member has a protected role or unsafe health state.");

        var diskIds = closure.Objects.Where(item => item.ObjectType == FactObjectType.Disk)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var disk in snapshot.OsDisks.Where(item => diskIds.Contains(item.StableId)))
        {
            var raw = topology.RequireObject(new StorageObjectId(
                topology.SystemId, StorageObjectKind.OsDisk, disk.StableId));
            if (disk.IsBoot || disk.IsSystem || RequiredBoolean(raw, "IsClustered")
                || RequiredBoolean(raw, "IsReadOnly"))
                throw new InvalidDataException("The OS disk is clustered, read-only, or protected by Windows.");
            if (disk.IsOffline && command is not SetDiskOnlineCommand { Online: true })
                throw new InvalidDataException("An offline disk cannot receive this storage command.");
            if (disk.PartitionStyle is not ("RAW" or "GPT" or "MBR"))
                throw new InvalidDataException("The disk layout is outside the supported basic-disk scope.");
        }

        var partitionIds = closure.Objects.Where(item => item.ObjectType == FactObjectType.Partition)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var deletingPartition = command is DeletePartitionCommand;
        string[] selectedAccessPaths = [];
        if (command is DeletePartitionCommand delete)
        {
            if (delete.Partition.Existing is not { } target
                || target.System != topology.SystemId
                || target.Kind != StorageObjectKind.Partition
                || delete.Partition.CreatedByStep is not null
                || !partitionIds.Contains(target.ProviderKey))
                throw new InvalidDataException("Partition deletion needs one exact target in the current physical closure.");
            var selected = snapshot.Partitions.SingleOrDefault(item => item.StableId == target.ProviderKey)
                ?? throw new InvalidDataException("The selected partition is absent from the current snapshot.");
            if (selected.IsBoot || selected.IsSystem || selected.IsHidden
                || !Guid.TryParse(selected.PartitionTypeId, out var role)
                || role != BasicDataRole)
                throw new InvalidDataException("Only the selected ordinary BasicData partition may be deleted.");
            var source = topology.RequireObject(target);
            var paths = source.Field("AccessPaths");
            if (paths is not { ReadState: FieldReadState.Returned,
                    Value: { ValueKind: JsonValueKind.Array } value }
                || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("The selected partition's access paths are unknown.");
            selectedAccessPaths = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
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
            if (RequiredBoolean(raw, "IsReadOnly", nonData)
                || RequiredBoolean(raw, "IsOffline")
                || RequiredBoolean(raw, "IsShadowCopy", nonData))
                throw new InvalidDataException("A related partition is read-only, offline, or a shadow copy.");
            if (partition.PartitionTypeId is { Length: > 0 }
                && Guid.TryParse(partition.PartitionTypeId, out var type)
                && (type == Guid.Parse("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3")
                    || type == Guid.Parse("af9b60a0-1431-4f62-bc68-3311714a69ad")))
                throw new InvalidDataException("Dynamic-disk LDM partitions are outside this stage.");
        }

        var volumes = snapshot.Volumes.Where(item =>
            item.PartitionStableId is not null
            && partitionIds.Contains(item.PartitionStableId)
            && (deletingPartition || !string.IsNullOrWhiteSpace(item.FileSystem)
                && !item.FileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        encryptionProbe(volumes, cancellationToken);

        if (MayInterruptRuntime(command))
            RequireRuntimeIndependent(volumes, selectedAccessPaths);
    }

    private static bool RequiredBoolean(WinPoolSourceObject item, string name,
        bool allowNotApplicableNull = false)
    {
        var field = item.Field(name);
        if (allowNotApplicableNull && field is
            { ReadState: FieldReadState.Returned,
              Value: { ValueKind: JsonValueKind.Null } })
            return false;
        if (field is not { ReadState: FieldReadState.Returned,
            Value: { } value }
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("A required Windows safety field is unavailable: " + name);
        return value.ValueKind == JsonValueKind.True;
    }

    private static void RequireFullyDecryptedVolumes(
        IReadOnlyList<VolumeInfo> volumes,
        CancellationToken cancellationToken)
    {
        if (volumes.Count == 0) return;
        using var searcher = new ManagementObjectSearcher(
            @"root\CIMV2\Security\MicrosoftVolumeEncryption",
            "SELECT DeviceID,DriveLetter FROM Win32_EncryptableVolume");
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var candidates = results.Cast<ManagementObject>().ToArray();
        try
        {
            foreach (var volume in volumes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matches = candidates.Where(item => MatchesVolume(item, volume)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException("BitLocker state cannot be uniquely mapped to a related volume.");
                var instance = matches[0];
                using var conversionParameters = instance.GetMethodParameters("GetConversionStatus");
                conversionParameters["PrecisionFactor"] = 0U;
                using var conversion = instance.InvokeMethod("GetConversionStatus",
                    conversionParameters, null);
                using var lockStatus = instance.InvokeMethod("GetLockStatus", null, null);
                if (conversion is null || lockStatus is null
                    || Convert.ToUInt32(conversion["ReturnValue"]) != 0
                    || Convert.ToUInt32(lockStatus["ReturnValue"]) != 0
                    || Convert.ToUInt32(conversion["ConversionStatus"]) != 0
                    || Convert.ToUInt32(lockStatus["LockStatus"]) != 0)
                    throw new InvalidDataException("A related volume is encrypted, locked, or has unknown BitLocker state.");
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static bool MatchesVolume(ManagementObject item, VolumeInfo volume)
    {
        var driveLetter = item["DriveLetter"]?.ToString()?.Trim();
        var deviceId = item["DeviceID"]?.ToString()?.Trim();
        return (!string.IsNullOrWhiteSpace(volume.DriveLetter)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    driveLetter, volume.DriveLetter + ":"))
            || (!string.IsNullOrWhiteSpace(deviceId)
                && volume.AccessPaths.Any(path => StringComparer.OrdinalIgnoreCase.Equals(
                    path.TrimEnd('\\'), deviceId.TrimEnd('\\'))));
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
