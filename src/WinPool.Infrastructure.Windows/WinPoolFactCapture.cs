using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>Reads the fixed collector's CIM observations, before formatted report construction.</summary>
internal static class WinPoolFactCapture
{
    private static readonly IReadOnlyDictionary<string, FactObjectType> Classes = new Dictionary<string, FactObjectType>(StringComparer.Ordinal)
    {
        ["MSFT_StorageSubSystem"] = FactObjectType.StorageSubsystem, ["MSFT_StoragePool"] = FactObjectType.StoragePool,
        ["MSFT_StorageTier"] = FactObjectType.StorageTier, ["MSFT_PhysicalDisk"] = FactObjectType.PhysicalDisk,
        ["MSFT_VirtualDisk"] = FactObjectType.VirtualDisk, ["MSFT_Disk"] = FactObjectType.Disk,
        ["MSFT_Partition"] = FactObjectType.Partition, ["MSFT_Volume"] = FactObjectType.Volume,
        ["Win32_ComputerSystem"] = FactObjectType.Computer, ["Win32_OperatingSystem"] = FactObjectType.OperatingSystem,
        ["Win32_BaseBoard"] = FactObjectType.BaseBoard, ["Win32_BIOS"] = FactObjectType.Bios,
        ["Win32_Processor"] = FactObjectType.Processor, ["Win32_CacheMemory"] = FactObjectType.CpuCache,
        ["Win32_PhysicalMemoryArray"] = FactObjectType.MemoryArray, ["Win32_PhysicalMemory"] = FactObjectType.MemoryModule,
        ["Win32_PageFileSetting"] = FactObjectType.PageFileSetting, ["Win32_PageFileUsage"] = FactObjectType.PageFileUsage,
        ["Win32_VideoController"] = FactObjectType.VideoController, ["Win32_DesktopMonitor"] = FactObjectType.Monitor,
        ["WmiMonitorID"] = FactObjectType.Monitor, ["MSFT_NetAdapter"] = FactObjectType.NetworkAdapter,
        ["Win32_NetworkAdapter"] = FactObjectType.NetworkAdapter, ["Win32_Battery"] = FactObjectType.Battery,
        ["Win32_LogicalDisk"] = FactObjectType.NetworkDisk
    };

    public static WinPoolFacts Read(JsonElement root, StorageSnapshot snapshot, SystemId systemId, CollectionPurpose purpose)
    {
        var sources = new Dictionary<string, WinPoolSource>(StringComparer.Ordinal);
        var objects = ImmutableArray.CreateBuilder<WinPoolSourceObject>();
        var bindings = ImmutableArray.CreateBuilder<WinPoolIdentityBinding>();
        string Namespace(string value) => value.Replace('\\', '/').ToLowerInvariant();
        string SourceId(string ns, string cls) => WinPoolIdentityRegistry.OpaqueSourceIdentity(ns + ":" + cls, "capture", snapshot.ScannedAt.ToString("O"));
        if (root.TryGetProperty("SourceQuerySuccesses", out var successes) && successes.ValueKind == JsonValueKind.Array)
        foreach (var success in successes.EnumerateArray())
        {
            var cls = success.GetProperty("ClassName").GetString() ?? "";
            if (!Classes.ContainsKey(cls)) continue;
            var ns = Namespace(success.GetProperty("Namespace").GetString() ?? "");
            var id = SourceId(ns, cls);
            sources.TryAdd(id, new(id, cls.StartsWith("MSFT_", StringComparison.Ordinal) ? FactOrigin.StorageCim : FactOrigin.Win32,
                ns, cls, snapshot.ScannedAt, purpose));
        }
        if (root.TryGetProperty("SourceObservations", out var observations) && observations.ValueKind == JsonValueKind.Array)
        foreach (var observation in observations.EnumerateArray())
        {
            var className = observation.GetProperty("ClassName").GetString() ?? string.Empty;
            if (!Classes.TryGetValue(className, out var type)) continue;
            var sourceNamespace = Namespace(observation.GetProperty("Namespace").GetString() ?? string.Empty);
            var sourceRef = SourceId(sourceNamespace, className);
            sources.TryAdd(sourceRef, new(sourceRef, className.StartsWith("MSFT_", StringComparison.Ordinal)
                ? FactOrigin.StorageCim : FactOrigin.Win32, sourceNamespace, className, snapshot.ScannedAt, purpose));
            var fields = observation.GetProperty("Fields").EnumerateArray().Select(field => ReadField(field, sourceRef)).ToImmutableArray();
            string Text(string name) => fields.FirstOrDefault(x => x.Name == name)?.Value is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
            var uniqueId = Text(type == FactObjectType.Partition ? "Guid" : "UniqueId");
            var objectId = Text("ObjectId");
            var rawIdentity = !string.IsNullOrWhiteSpace(uniqueId) ? uniqueId : objectId;
            if (rawIdentity.Length == 0 && !className.StartsWith("MSFT_", StringComparison.Ordinal)
                && observation.TryGetProperty("Identity", out var key)) rawIdentity = key.GetString() ?? "";
            var reliable = !string.IsNullOrWhiteSpace(rawIdentity);
            var opaque = reliable ? WinPoolIdentityRegistry.OpaqueSourceIdentity(sourceNamespace, className, rawIdentity) : string.Empty;
            var prefix = type switch
            {
                FactObjectType.StorageSubsystem => "subsystem", FactObjectType.StoragePool => "pool", FactObjectType.StorageTier => "tier",
                FactObjectType.PhysicalDisk => "physical", FactObjectType.VirtualDisk => "virtual", FactObjectType.Disk => "osdisk",
                FactObjectType.Partition => "partition", FactObjectType.Volume => "volume", _ => type.ToString()
            };
            var id = !reliable ? "temporary:" + Guid.NewGuid().ToString("N")
                : uniqueId.Length > 0 || objectId.Length > 0 ? StableId.Create(prefix, uniqueId, objectId).Value
                : WinPoolIdentityRegistry.OpaqueSourceIdentity(sourceNamespace, className, rawIdentity);
            if (objects.Any(x => x.Id == id)) continue;
            objects.Add(new(id, type, sourceRef, opaque, reliable, fields));
            if (reliable) bindings.Add(new(type, opaque, id));
        }
        if (root.TryGetProperty("SourceQueryFailures", out var failures) && failures.ValueKind == JsonValueKind.Array)
        foreach (var failure in failures.EnumerateArray())
        {
            var className = failure.GetProperty("ClassName").GetString() ?? "";
            if (!Classes.ContainsKey(className)) continue;
            var ns = Namespace(failure.TryGetProperty("Namespace", out var namespaceValue) ? namespaceValue.GetString() ?? "" : "root/cimv2");
            var id = SourceId(ns, className);
            sources[id] = new(id, className.StartsWith("MSFT_", StringComparison.Ordinal) ? FactOrigin.StorageCim : FactOrigin.Win32,
                ns, className, snapshot.ScannedAt, purpose, FieldReadState.Failed, "QueryFailed");
        }
        var relationships = ImmutableArray.CreateBuilder<WinPoolFactRelationship>();
        var ids = objects.Select(x => x.Id).ToHashSet();
        void Link(string? from, string? to, string kind)
        {
            if (from is not null && to is not null && ids.Contains(from) && ids.Contains(to)) relationships.Add(new(from, to, kind));
        }
        foreach (var pool in snapshot.StoragePools)
        {
            Link(pool.SubsystemStableId, pool.StableId, "subsystem-pool");
            foreach (var member in pool.MemberPhysicalDiskIds) Link(pool.StableId, member, "pool-member");
        }
        foreach (var disk in snapshot.VirtualDisks) Link(disk.PoolStableId, disk.StableId, "pool-virtual-disk");
        foreach (var disk in snapshot.OsDisks)
        {
            Link(disk.PhysicalDiskStableId, disk.StableId, "same-device");
            Link(disk.VirtualDiskStableId, disk.StableId, "same-device");
        }
        foreach (var partition in snapshot.Partitions) Link(partition.OsDiskStableId, partition.StableId, "disk-partition");
        foreach (var volume in snapshot.Volumes) Link(volume.PartitionStableId, volume.StableId, "partition-volume");
        foreach (var tier in snapshot.StorageTiers)
        {
            Link(tier.PoolStableId, tier.StableId, "pool-tier");
            Link(tier.VirtualDiskStableId, tier.StableId, "virtual-disk-tier");
        }
        var state = sources.Values.Any(x => x.ReadState == FieldReadState.Failed) ? FieldReadState.Failed : FieldReadState.Returned;
        var facts = new WinPoolFacts(WinPoolFacts.CurrentFormatVersion, systemId, 0, sources.Values.ToImmutableArray(), objects.ToImmutable(),
            relationships.ToImmutable(), bindings.ToImmutable(), [new(purpose, snapshot.ScannedAt, DateTimeOffset.Now, state)])
        {
            InventoryVersion = snapshot.SnapshotVersion, InventoryCapturedAt = snapshot.ScannedAt
        };
        facts.Validate();
        return facts;
    }

    private static WinPoolSourceField ReadField(JsonElement field, string sourceRef)
    {
        var name = field.GetProperty("Name").GetString()!;
        var cimType = field.GetProperty("CimType").GetString() ?? "String";
        var state = Enum.Parse<FieldReadState>(field.GetProperty("ReadState").GetString()!);
        var array = cimType.EndsWith("Array", StringComparison.Ordinal);
        var type = cimType.StartsWith("UInt", StringComparison.Ordinal) ? (array ? FactValueType.UInt64Array : FactValueType.UInt64)
            : cimType.StartsWith("SInt", StringComparison.Ordinal) ? (array ? FactValueType.Int64Array : FactValueType.Int64)
            : cimType == "Boolean" ? FactValueType.Boolean
            : cimType is "Real32" or "Real64" ? FactValueType.Decimal
            : array ? FactValueType.StringArray : FactValueType.String;
        var value = state == FieldReadState.Returned ? field.GetProperty("Value").Clone() : (JsonElement?)null;
        var unit = name is "Size" or "SizeRemaining" or "AllocatedSize" or "FootprintOnPool" or "Offset"
            or "AllocationUnitSize" or "LogicalSectorSize" or "PhysicalSectorSize" or "Interleave" ? "bytes" : null;
        return new(name, type, value, state, sourceRef, unit,
            field.TryGetProperty("ReasonCode", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null);
    }
}
