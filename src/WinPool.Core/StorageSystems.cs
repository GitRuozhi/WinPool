using System.Text.Json;

namespace WinPool.Core;

public enum StorageSystemKind
{
    Local,
    Simulation
}

public enum CollectorSourceStatus
{
    Success,
    NoData,
    Failed,
    Unavailable
}

public sealed record CollectorSourceResult(
    string Source,
    CollectorSourceStatus Status,
    JsonElement? RawValue,
    string Error,
    long DurationMilliseconds);

public sealed record HardwareInventoryItemResult(
    string Id,
    string Category,
    string StandardName,
    string ChineseName,
    JsonElement? FinalValue,
    IReadOnlyList<CollectorSourceResult> Sources,
    IReadOnlyList<string> Warnings);

public sealed record HardwareInventoryReport(
    int SchemaVersion,
    DateTimeOffset CollectedAt,
    IReadOnlyList<HardwareInventoryItemResult> Items,
    IReadOnlyList<string> Warnings)
{
    public static HardwareInventoryReport Empty(DateTimeOffset collectedAt) =>
        new(1, collectedAt, [], []);
}

public enum SimulationJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

public sealed record SimulationJob(
    string Id,
    string Operation,
    string TargetStableId,
    SimulationJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string Message);

public sealed record StorageSystemDocument(
    int SchemaVersion,
    string Id,
    StorageSystemKind Kind,
    string DisplayName,
    StorageSnapshot Snapshot,
    HardwareInventoryReport HardwareReport,
    IReadOnlyList<SimulationJob> Jobs,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsLocal => Kind == StorageSystemKind.Local;

    public StorageSystemDocument AsImportedSimulation(string? displayName = null) =>
        this with
        {
            Id = $"simulation:{Guid.NewGuid():N}",
            Kind = StorageSystemKind.Simulation,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? DisplayName : displayName.Trim(),
            UpdatedAt = DateTimeOffset.Now
        };
}

public static class StorageSystemDocumentSanitizer
{
    private static readonly HashSet<string> SensitiveItemIds =
    [
        "0304",
        "0510",
        "0718",
        "0803",
        "1206"
    ];

    public static bool IsSensitiveItemId(string id) => SensitiveItemIds.Contains(id);

    public static StorageSystemDocument RedactSensitiveData(StorageSystemDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var snapshot = document.Snapshot with
        {
            PhysicalDisks = document.Snapshot.PhysicalDisks
                .Select(disk => disk with
                {
                    MaskedSerialNumber = MaskOnce(disk.MaskedSerialNumber)
                })
                .ToArray()
        };
        var report = document.HardwareReport with
        {
            Items = document.HardwareReport.Items.Select(item =>
            {
                if (!IsSensitive(item))
                {
                    return item;
                }
                return item with
                {
                    FinalValue = MaskElement(item.FinalValue),
                    Sources = item.Sources.Select(source => source with
                    {
                        RawValue = MaskElement(source.RawValue)
                    }).ToArray()
                };
            }).ToArray()
        };
        return document with { Snapshot = snapshot, HardwareReport = report };
    }

    private static bool IsSensitive(HardwareInventoryItemResult item) =>
        SensitiveItemIds.Contains(item.Id)
        || item.StandardName.Contains("Serial", StringComparison.OrdinalIgnoreCase)
        || item.StandardName.Contains("MAC Address", StringComparison.OrdinalIgnoreCase)
        || item.ChineseName.Contains("序列", StringComparison.Ordinal);

    private static JsonElement? MaskElement(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteMasked(writer, element.Value);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteMasked(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteMasked(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteMasked(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(MaskOnce(element.GetString()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string MaskOnce(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value == "—"
        || value.Contains('•')
            ? value ?? string.Empty
            : StableId.MaskSerial(value);
}

public sealed class StorageSystemCatalog
{
    private readonly List<StorageSystemDocument> _systems = [];

    public IReadOnlyList<StorageSystemDocument> Systems => _systems;

    public void ReplaceLocal(StorageSystemDocument local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (!local.IsLocal)
        {
            throw new ArgumentException("The local catalog entry must have Local kind.", nameof(local));
        }

        _systems.RemoveAll(x => x.IsLocal);
        _systems.Insert(0, local);
    }

    public void ReplaceSimulations(IEnumerable<StorageSystemDocument> simulations)
    {
        _systems.RemoveAll(x => !x.IsLocal);
        _systems.AddRange(simulations.Where(x => !x.IsLocal));
        NormalizeOrder();
    }

    public void AddSimulation(StorageSystemDocument simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (simulation.IsLocal)
        {
            throw new ArgumentException("A local system cannot be added as a simulation.", nameof(simulation));
        }

        _systems.Add(simulation);
        NormalizeOrder();
    }

    public void Update(StorageSystemDocument document)
    {
        var index = _systems.FindIndex(x => x.Id == document.Id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Storage system '{document.Id}' is not in the catalog.");
        }

        _systems[index] = document;
        NormalizeOrder();
    }

    public StorageSystemDocument? Find(string id) =>
        _systems.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private void NormalizeOrder()
    {
        var local = _systems.Where(x => x.IsLocal).Take(1);
        var simulations = _systems.Where(x => !x.IsLocal);
        var normalized = local.Concat(simulations).ToArray();
        _systems.Clear();
        _systems.AddRange(normalized);
    }
}

public enum SimulationOperationKind
{
    Rename,
    ChangeDriveLetter,
    FormatPartition,
    DeletePartition,
    ConvertDisk,
    SetDiskOffline,
    OptimizePool,
    InitializeDisk,
    CreatePartition,
    ExtendPartition,
    ShrinkPartition,
    CreateStoragePool,
    CreateVirtualDisk,
    MovePhysicalDisk,
    OptimizeDrive
}

public sealed record SimulationOperationRequest(
    SimulationOperationKind Kind,
    string TargetStableId,
    string? Name = null,
    string? DriveLetter = null,
    string? FileSystem = null,
    long? AllocationUnitSize = null,
    bool? Offline = null,
    long? SizeBytes = null,
    bool? CreateMsr = null,
    long? InterleaveBytes = null,
    string? Resiliency = null,
    IReadOnlyList<string>? MemberDiskIds = null);

public sealed record SimulationOperationResult(
    bool Succeeded,
    StorageSystemDocument Document,
    string Error,
    IReadOnlyList<string> Commands)
{
    public static SimulationOperationResult Failure(StorageSystemDocument document, string error) =>
        new(false, document, error, []);
}

public static class SimulatedCommandText
{
    public static IReadOnlyList<string> Build(SimulationOperationRequest request) => request.Kind switch
    {
        SimulationOperationKind.Rename =>
            [$"Set-StorageObject -FriendlyName '{request.Name}'"],
        SimulationOperationKind.ChangeDriveLetter =>
            [$"Set-Partition -NewDriveLetter {TopologyProjector.NormalizeDriveLetter(request.DriveLetter)}"],
        SimulationOperationKind.FormatPartition =>
            [$"Format-Volume -FileSystem {request.FileSystem?.Trim().ToUpperInvariant()} -AllocationUnitSize {request.AllocationUnitSize ?? 4096} -Confirm:$false"],
        SimulationOperationKind.DeletePartition =>
            ["Remove-Partition -Confirm:$false"],
        SimulationOperationKind.ConvertDisk =>
            [$"Set-Disk -PartitionStyle {request.Name?.Trim().ToUpperInvariant()}"],
        SimulationOperationKind.SetDiskOffline =>
            [$"Set-Disk -IsOffline ${(request.Offline == true ? "true" : "false")}"],
        SimulationOperationKind.OptimizePool =>
            ["Optimize-StoragePool"],
        SimulationOperationKind.InitializeDisk =>
            BuildInitialize(request),
        SimulationOperationKind.CreatePartition =>
            [$"New-Partition -Size {FormatSize(request.SizeBytes)} -AssignDriveLetter"],
        SimulationOperationKind.ExtendPartition =>
            [$"Resize-Partition -Size {FormatSize(request.SizeBytes)}"],
        SimulationOperationKind.ShrinkPartition =>
            [$"Resize-Partition -Size {FormatSize(request.SizeBytes)}"],
        SimulationOperationKind.CreateStoragePool =>
            [$"New-StoragePool -FriendlyName '{request.Name}' -StorageSubsystemFriendlyName 'Windows Storage*' -PhysicalDisks ({request.MemberDiskIds?.Count ?? 0} disks)"],
        SimulationOperationKind.CreateVirtualDisk =>
            [$"New-VirtualDisk -FriendlyName '{request.Name}' -Interleave {request.InterleaveBytes ?? 65536} -ResiliencySettingName {request.Resiliency ?? "Simple"}",
             $"New-Partition -AssignDriveLetter | Format-Volume -FileSystem NTFS -AllocationUnitSize {request.AllocationUnitSize ?? 65536} -Confirm:$false"],
        SimulationOperationKind.MovePhysicalDisk =>
            ["Add-PhysicalDisk / Remove-PhysicalDisk (move between pools)"],
        SimulationOperationKind.OptimizeDrive =>
            [$"Optimize-Volume"],
        _ => []
    };

    private static IReadOnlyList<string> BuildInitialize(SimulationOperationRequest request)
    {
        var commands = new List<string>
        {
            "Clear-Disk -RemoveData -Confirm:$false",
            $"Initialize-Disk -PartitionStyle {request.Name?.Trim().ToUpperInvariant()}"
        };
        if (request.CreateMsr == true)
        {
            commands.Add("New-Partition -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'");
        }
        return commands;
    }

    private static string FormatSize(long? bytes) =>
        bytes is null or <= 0 ? "(remaining)" : $"{bytes}";
}

public sealed class SimulationOperationService : ISimulationOperationService
{
    public SimulationOperationResult Apply(
        StorageSystemDocument document,
        SimulationOperationRequest request)
    {
        if (document.Kind != StorageSystemKind.Simulation)
        {
            return SimulationOperationResult.Failure(document, "Local storage systems are read-only.");
        }

        try
        {
            var snapshot = request.Kind switch
            {
                SimulationOperationKind.Rename => Rename(document.Snapshot, request),
                SimulationOperationKind.ChangeDriveLetter => ChangeDriveLetter(document.Snapshot, request),
                SimulationOperationKind.FormatPartition => FormatPartition(document.Snapshot, request),
                SimulationOperationKind.DeletePartition => DeletePartition(document.Snapshot, request),
                SimulationOperationKind.ConvertDisk => ConvertDisk(document.Snapshot, request),
                SimulationOperationKind.SetDiskOffline => SetDiskOffline(document.Snapshot, request),
                SimulationOperationKind.InitializeDisk => InitializeDisk(document.Snapshot, request),
                SimulationOperationKind.CreatePartition => CreatePartition(document.Snapshot, request),
                SimulationOperationKind.ExtendPartition => ResizePartition(document.Snapshot, request, extend: true),
                SimulationOperationKind.ShrinkPartition => ResizePartition(document.Snapshot, request, extend: false),
                SimulationOperationKind.CreateStoragePool => CreateStoragePool(document.Snapshot, request),
                SimulationOperationKind.CreateVirtualDisk => CreateVirtualDisk(document.Snapshot, request),
                SimulationOperationKind.MovePhysicalDisk => MovePhysicalDisk(document.Snapshot, request),
                SimulationOperationKind.OptimizePool or SimulationOperationKind.OptimizeDrive => document.Snapshot,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };

            var jobs = document.Jobs;
            if (request.Kind is SimulationOperationKind.OptimizePool or SimulationOperationKind.OptimizeDrive)
            {
                var now = DateTimeOffset.Now;
                if (request.Kind == SimulationOperationKind.OptimizePool)
                {
                    var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == request.TargetStableId);
                    if (pool is null || pool.IsPrimordial)
                    {
                        throw new InvalidOperationException("Only a non-primordial simulated pool can be optimized.");
                    }
                    jobs = jobs.Append(new SimulationJob(
                        $"job:{Guid.NewGuid():N}",
                        "OptimizeStoragePool",
                        pool.StableId,
                        SimulationJobStatus.Completed,
                        now,
                        now,
                        "Simulated storage-pool allocation rebalance completed.")).ToArray();
                }
                else
                {
                    jobs = jobs.Append(new SimulationJob(
                        $"job:{Guid.NewGuid():N}",
                        "OptimizeVolume",
                        request.TargetStableId,
                        SimulationJobStatus.Completed,
                        now,
                        now,
                        "Simulated drive optimization completed.")).ToArray();
                }
            }

            return new SimulationOperationResult(
                true,
                document with { Snapshot = snapshot, Jobs = jobs, UpdatedAt = DateTimeOffset.Now },
                string.Empty,
                SimulatedCommandText.Build(request));
        }
        catch (InvalidOperationException ex)
        {
            return SimulationOperationResult.Failure(document, ex.Message);
        }
    }

    private static StorageSnapshot Rename(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A non-empty name is required.");
        }

        if (snapshot.StoragePools.Any(x => x.StableId == request.TargetStableId))
        {
            var target = snapshot.StoragePools.First(x => x.StableId == request.TargetStableId);
            if (target.IsPrimordial)
            {
                throw new InvalidOperationException("The primordial pool cannot be renamed.");
            }
            return snapshot with
            {
                StoragePools = snapshot.StoragePools
                    .Select(x => x.StableId == request.TargetStableId ? x with { FriendlyName = name } : x)
                    .ToArray()
            };
        }

        if (snapshot.StorageTiers.Any(x => x.StableId == request.TargetStableId))
        {
            return snapshot with
            {
                StorageTiers = snapshot.StorageTiers
                    .Select(x => x.StableId == request.TargetStableId ? x with { FriendlyName = name } : x)
                    .ToArray()
            };
        }

        if (snapshot.PhysicalDisks.Any(x => x.StableId == request.TargetStableId))
        {
            return snapshot with
            {
                PhysicalDisks = snapshot.PhysicalDisks
                    .Select(x => x.StableId == request.TargetStableId ? x with { FriendlyName = name } : x)
                    .ToArray()
            };
        }

        if (snapshot.VirtualDisks.Any(x => x.StableId == request.TargetStableId))
        {
            return snapshot with
            {
                VirtualDisks = snapshot.VirtualDisks
                    .Select(x => x.StableId == request.TargetStableId ? x with { FriendlyName = name } : x)
                    .ToArray()
            };
        }

        if (snapshot.OsDisks.Any(x => x.StableId == request.TargetStableId))
        {
            return snapshot with
            {
                OsDisks = snapshot.OsDisks
                    .Select(x => x.StableId == request.TargetStableId ? x with { FriendlyName = name } : x)
                    .ToArray()
            };
        }

        if (snapshot.Partitions.Any(x => x.StableId == request.TargetStableId))
        {
            return snapshot with
            {
                Partitions = snapshot.Partitions
                    .Select(x => x.StableId == request.TargetStableId ? x with { FileSystemLabel = name } : x)
                    .ToArray()
            };
        }

        throw new InvalidOperationException("The selected simulated object cannot be renamed.");
    }

    private static StorageSnapshot ChangeDriveLetter(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var driveLetter = TopologyProjector.NormalizeDriveLetter(request.DriveLetter);
        if (driveLetter.Length != 1)
        {
            throw new InvalidOperationException("A drive letter from A through Z is required.");
        }
        if (snapshot.Partitions.Any(x =>
                x.StableId != request.TargetStableId
                && TopologyProjector.NormalizeDriveLetter(x.DriveLetter) == driveLetter))
        {
            throw new InvalidOperationException($"Drive letter {driveLetter}: is already in use.");
        }

        var found = false;
        var partitions = snapshot.Partitions.Select(x =>
        {
            if (x.StableId != request.TargetStableId)
            {
                return x;
            }
            found = true;
            return x with { DriveLetter = driveLetter, Path = $"{driveLetter}:\\" };
        }).ToArray();
        if (!found)
        {
            throw new InvalidOperationException("The selected partition was not found.");
        }
        return snapshot with { Partitions = partitions };
    }

    private static StorageSnapshot FormatPartition(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var fileSystem = request.FileSystem?.Trim().ToUpperInvariant();
        if (fileSystem is not ("NTFS" or "REFS" or "EXFAT"))
        {
            throw new InvalidOperationException("The simulated file system must be NTFS, ReFS, or exFAT.");
        }
        var allocationUnit = request.AllocationUnitSize ?? 4096;
        if (allocationUnit <= 0)
        {
            throw new InvalidOperationException("Allocation unit size must be positive.");
        }

        var found = false;
        var partitions = snapshot.Partitions.Select(x =>
        {
            if (x.StableId != request.TargetStableId)
            {
                return x;
            }
            if (x.IsBoot || x.IsSystem)
            {
                throw new InvalidOperationException("A simulated boot or system partition cannot be formatted.");
            }
            found = true;
            return x with
            {
                FileSystem = fileSystem,
                FileSystemLabel = request.Name?.Trim() ?? x.FileSystemLabel,
                AllocationUnitSize = allocationUnit,
                SizeRemaining = x.Size,
                HealthStatus = "Healthy",
                OperationalStatus = "OK"
            };
        }).ToArray();
        if (!found)
        {
            throw new InvalidOperationException("The selected partition was not found.");
        }
        return snapshot with { Partitions = partitions };
    }

    private static StorageSnapshot DeletePartition(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected partition was not found.");
        if (partition.IsBoot || partition.IsSystem)
        {
            throw new InvalidOperationException("A simulated boot or system partition cannot be deleted.");
        }
        return snapshot with
        {
            Partitions = snapshot.Partitions.Where(x => x.StableId != request.TargetStableId).ToArray()
        };
    }

    private static StorageSnapshot ConvertDisk(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var style = request.Name?.Trim().ToUpperInvariant();
        if (style is not ("GPT" or "MBR"))
        {
            throw new InvalidOperationException("Partition style must be GPT or MBR.");
        }
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected OS disk was not found.");
        if (snapshot.Partitions.Any(x => x.OsDiskStableId == disk.StableId))
        {
            throw new InvalidOperationException("Only an empty simulated disk can be converted.");
        }
        return snapshot with
        {
            OsDisks = snapshot.OsDisks
                .Select(x => x.StableId == disk.StableId ? x with { PartitionStyle = style } : x)
                .ToArray()
        };
    }

    private static StorageSnapshot SetDiskOffline(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var offline = request.Offline
            ?? throw new InvalidOperationException("The requested online state is missing.");
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected OS disk was not found.");
        var physical = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == disk.PhysicalDiskStableId);
        if (offline && (disk.IsBoot || disk.IsSystem
                        || physical?.IsPageFile == true || physical?.IsCrashDump == true))
        {
            throw new InvalidOperationException("Boot, system, page-file, and crash-dump disks cannot be taken offline.");
        }
        return snapshot with
        {
            OsDisks = snapshot.OsDisks
                .Select(x => x.StableId == disk.StableId ? x with { IsOffline = offline } : x)
                .ToArray()
        };
    }

    private static StorageSnapshot InitializeDisk(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var style = request.Name?.Trim().ToUpperInvariant();
        if (style is not ("GPT" or "MBR"))
        {
            throw new InvalidOperationException("Partition style must be GPT or MBR.");
        }
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected OS disk was not found.");
        if (disk.IsBoot || disk.IsSystem)
        {
            throw new InvalidOperationException("The simulated boot or system disk cannot be initialized.");
        }

        var remaining = snapshot.Partitions.Where(x => x.OsDiskStableId != disk.StableId).ToList();
        if (style == "GPT" && request.CreateMsr == true)
        {
            remaining.Add(new PartitionInfo(
                $"sim:partition:{Guid.NewGuid():N}",
                true,
                disk.Number,
                1,
                "MicrosoftReserved",
                17408,
                16 * 1024 * 1024,
                false,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                0,
                "Healthy",
                "OK",
                string.Empty,
                disk.StableId));
        }

        return snapshot with
        {
            OsDisks = snapshot.OsDisks
                .Select(x => x.StableId == disk.StableId
                    ? x with { PartitionStyle = style, IsOffline = false }
                    : x)
                .ToArray(),
            Partitions = remaining
        };
    }

    private static StorageSnapshot CreatePartition(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected OS disk was not found.");
        if (disk.IsOffline)
        {
            throw new InvalidOperationException("An offline disk cannot accept a new partition.");
        }

        var existing = snapshot.Partitions
            .Where(x => x.OsDiskStableId == disk.StableId)
            .OrderBy(x => x.Offset)
            .ToList();
        var offset = existing.Count == 0
            ? 1024L * 1024
            : existing.Max(x => x.Offset + x.Size);
        var free = disk.Size - offset;
        var size = request.SizeBytes is null or <= 0 ? free : Math.Min(request.SizeBytes.Value, free);
        if (size <= 0)
        {
            throw new InvalidOperationException("The simulated disk has no free space for a new partition.");
        }

        var letter = string.IsNullOrWhiteSpace(request.DriveLetter)
            ? NextFreeDriveLetter(snapshot)
            : TopologyProjector.NormalizeDriveLetter(request.DriveLetter);
        var partition = new PartitionInfo(
            $"sim:partition:{Guid.NewGuid():N}",
            true,
            disk.Number,
            existing.Count + 1,
            "Primary",
            offset,
            size,
            false,
            false,
            letter,
            request.Name?.Trim() ?? string.Empty,
            request.FileSystem?.Trim().ToUpperInvariant() ?? string.Empty,
            string.IsNullOrWhiteSpace(request.FileSystem) ? null : request.AllocationUnitSize ?? 4096,
            size,
            "Healthy",
            "OK",
            string.IsNullOrWhiteSpace(letter) ? string.Empty : $"{letter}:\\",
            disk.StableId);

        return snapshot with { Partitions = snapshot.Partitions.Append(partition).ToArray() };
    }

    private static StorageSnapshot ResizePartition(
        StorageSnapshot snapshot,
        SimulationOperationRequest request,
        bool extend)
    {
        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected partition was not found.");
        if (partition.IsBoot || partition.IsSystem)
        {
            throw new InvalidOperationException("A simulated boot or system partition cannot be resized.");
        }
        var newSize = request.SizeBytes
            ?? throw new InvalidOperationException("The requested size is missing.");
        var used = partition.Size - partition.SizeRemaining;
        if (newSize < used)
        {
            throw new InvalidOperationException("The simulated partition cannot be smaller than its used space.");
        }
        if (extend && newSize <= partition.Size)
        {
            throw new InvalidOperationException("Extending requires a size larger than the current partition.");
        }
        if (!extend && newSize >= partition.Size)
        {
            throw new InvalidOperationException("Shrinking requires a size smaller than the current partition.");
        }
        if (extend)
        {
            var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
            if (disk is not null && partition.Offset + newSize > disk.Size)
            {
                throw new InvalidOperationException("The simulated disk does not have enough free space to extend.");
            }
        }

        return snapshot with
        {
            Partitions = snapshot.Partitions
                .Select(x => x.StableId == partition.StableId
                    ? x with { Size = newSize, SizeRemaining = newSize - used }
                    : x)
                .ToArray()
        };
    }

    private static StorageSnapshot CreateStoragePool(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A non-empty pool name is required.");
        }
        if (snapshot.StoragePools.Any(x =>
                !x.IsPrimordial && x.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A simulated pool named '{name}' already exists.");
        }

        var memberIds = (request.MemberDiskIds ?? []).ToArray();
        if (memberIds.Length == 0)
        {
            throw new InvalidOperationException("At least one physical disk is required for a simulated pool.");
        }
        var primordial = snapshot.StoragePools.FirstOrDefault(x => x.IsPrimordial)
            ?? throw new InvalidOperationException("The simulated system has no primordial pool.");
        var members = snapshot.PhysicalDisks
            .Where(x => memberIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (members.Length != memberIds.Length)
        {
            throw new InvalidOperationException("A selected physical disk was not found.");
        }
        if (members.Any(x => x.PoolStableId != primordial.StableId))
        {
            throw new InvalidOperationException("Only primordial disks can join a new simulated pool.");
        }
        if (members.Any(x => x.IsBoot || x.IsSystem || x.IsPageFile || x.IsCrashDump))
        {
            throw new InvalidOperationException("Boot, system, page-file, and crash-dump disks cannot join a simulated pool.");
        }

        var poolId = $"sim:pool:{Guid.NewGuid():N}";
        var pool = new StoragePoolInfo(
            poolId,
            true,
            name,
            false,
            "Healthy",
            "OK",
            members.Sum(x => x.Size),
            0,
            primordial.SubsystemStableId,
            memberIds);

        return snapshot with
        {
            StoragePools = snapshot.StoragePools
                .Select(x => x.IsPrimordial
                    ? x with
                    {
                        MemberPhysicalDiskIds = x.MemberPhysicalDiskIds
                            .Where(id => !memberIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                            .ToArray()
                    }
                    : x)
                .Append(pool)
                .ToArray(),
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(x => memberIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase)
                    ? x with { PoolStableId = poolId, CanPool = false }
                    : x)
                .ToArray()
        };
    }

    private static StorageSnapshot CreateVirtualDisk(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected pool was not found.");
        if (pool.IsPrimordial)
        {
            throw new InvalidOperationException("A simulated virtual disk requires a non-primordial pool.");
        }
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A non-empty virtual disk name is required.");
        }

        var free = pool.Size - pool.AllocatedSize;
        var size = request.SizeBytes is null or <= 0 ? free : Math.Min(request.SizeBytes.Value, free);
        if (size <= 0)
        {
            throw new InvalidOperationException("The simulated pool has no free capacity.");
        }

        var vdiskId = $"sim:vdisk:{Guid.NewGuid():N}";
        var osDiskNumber = snapshot.OsDisks.Select(x => x.Number).DefaultIfEmpty(-1).Max() + 1;
        var vdisk = new VirtualDiskInfo(
            vdiskId,
            true,
            name,
            "Healthy",
            "OK",
            request.Resiliency ?? "Simple",
            "Fixed",
            1,
            request.InterleaveBytes ?? 65536,
            size,
            size,
            pool.StableId,
            [],
            [osDiskNumber]);
        var osDisk = new OsDiskInfo(
            $"sim:osdisk:{Guid.NewGuid():N}",
            name,
            osDiskNumber,
            "RAW",
            size,
            false,
            false,
            false,
            null,
            vdiskId);

        return snapshot with
        {
            VirtualDisks = snapshot.VirtualDisks.Append(vdisk).ToArray(),
            OsDisks = snapshot.OsDisks.Append(osDisk).ToArray(),
            StoragePools = snapshot.StoragePools
                .Select(x => x.StableId == pool.StableId
                    ? x with { AllocatedSize = x.AllocatedSize + size }
                    : x)
                .ToArray()
        };
    }

    private static StorageSnapshot MovePhysicalDisk(StorageSnapshot snapshot, SimulationOperationRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == request.TargetStableId)
            ?? throw new InvalidOperationException("The selected physical disk was not found.");
        if (disk.IsBoot || disk.IsSystem || disk.IsPageFile || disk.IsCrashDump)
        {
            throw new InvalidOperationException("Boot, system, page-file, and crash-dump disks cannot move between pools.");
        }
        if (snapshot.StorageTiers.Any(x => x.MemberPhysicalDiskIds.Contains(
                disk.StableId, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A disk used by a storage tier cannot move between pools.");
        }

        var primordial = snapshot.StoragePools.FirstOrDefault(x => x.IsPrimordial)
            ?? throw new InvalidOperationException("The simulated system has no primordial pool.");
        var targetId = string.IsNullOrWhiteSpace(request.Name)
            ? primordial.StableId
            : request.Name.Trim();
        var target = snapshot.StoragePools.FirstOrDefault(x => x.StableId == targetId)
            ?? throw new InvalidOperationException("The target pool was not found.");
        if (disk.PoolStableId == target.StableId)
        {
            return snapshot;
        }

        return snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(x => x.StableId == disk.StableId
                    ? x with { PoolStableId = target.StableId, CanPool = target.IsPrimordial }
                    : x)
                .ToArray(),
            StoragePools = snapshot.StoragePools
                .Select(pool =>
                {
                    var members = pool.MemberPhysicalDiskIds
                        .Where(id => !id.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (pool.StableId == target.StableId)
                    {
                        members.Add(disk.StableId);
                    }
                    return pool with { MemberPhysicalDiskIds = members };
                })
                .ToArray()
        };
    }

    private static string NextFreeDriveLetter(StorageSnapshot snapshot)
    {
        var used = snapshot.Partitions
            .Select(x => TopologyProjector.NormalizeDriveLetter(x.DriveLetter))
            .Concat(snapshot.NetworkDisks.Select(x => TopologyProjector.NormalizeDriveLetter(x.DriveLetter)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in "CDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(candidate.ToString()))
            {
                return candidate.ToString();
            }
        }
        return string.Empty;
    }
}
