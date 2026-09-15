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
        ["Win32_VideoController"] = FactObjectType.HardwareSupplement, ["Win32_DesktopMonitor"] = FactObjectType.HardwareSupplement,
        ["WmiMonitorID"] = FactObjectType.HardwareSupplement, ["MSFT_NetAdapter"] = FactObjectType.NetworkAdapter,
        ["Win32_NetworkAdapter"] = FactObjectType.NetworkAdapter, ["Win32_Battery"] = FactObjectType.Battery,
        ["Win32_LogicalDisk"] = FactObjectType.LogicalDisk,
        ["Windows.DiskRoles"] = FactObjectType.HardwareSupplement,
        ["Win32_DiskDrive"] = FactObjectType.HardwareSupplement, ["Win32_TimeZone"] = FactObjectType.HardwareSupplement,
        ["SoftwareLicensingProduct"] = FactObjectType.HardwareSupplement,
        ["BatteryStaticData"] = FactObjectType.HardwareSupplement, ["BatteryStatus"] = FactObjectType.HardwareSupplement,
        ["Registry.CurrentVersion"] = FactObjectType.HardwareSupplement, ["Registry.VideoMemory"] = FactObjectType.HardwareSupplement,
        ["WindowsForms.Screen"] = FactObjectType.HardwareSupplement, ["Windows.Session"] = FactObjectType.HardwareSupplement
    };

    public static WinPoolFacts Read(JsonElement root, StorageSnapshot snapshot, SystemId systemId, CollectionPurpose purpose)
    {
        var sources = new Dictionary<string, WinPoolSource>(StringComparer.Ordinal);
        var objects = ImmutableArray.CreateBuilder<WinPoolSourceObject>();
        var bindings = ImmutableArray.CreateBuilder<WinPoolIdentityBinding>();
        string Namespace(string value) => value.Replace('\\', '/').ToLowerInvariant();
        FactOrigin Origin(string ns, string cls) => ns == "winpool/native" ? FactOrigin.Native
            : cls.StartsWith("MSFT_", StringComparison.Ordinal) ? FactOrigin.StorageCim : FactOrigin.Win32;
        string SourceId(string ns, string cls) => WinPoolIdentityRegistry.OpaqueSourceIdentity(ns + ":" + cls, "capture", snapshot.ScannedAt.ToString("O"));
        if (root.TryGetProperty("SourceQuerySuccesses", out var successes) && successes.ValueKind == JsonValueKind.Array)
        foreach (var success in successes.EnumerateArray())
        {
            var cls = success.GetProperty("ClassName").GetString() ?? "";
            if (!Classes.ContainsKey(cls)) continue;
            var ns = Namespace(success.GetProperty("Namespace").GetString() ?? "");
            var id = SourceId(ns, cls);
            sources.TryAdd(id, new(id, Origin(ns, cls),
                ns, cls, snapshot.ScannedAt, purpose));
        }
        if (root.TryGetProperty("SourceObservations", out var observations) && observations.ValueKind == JsonValueKind.Array)
        foreach (var observation in observations.EnumerateArray())
        {
            var className = observation.GetProperty("ClassName").GetString() ?? string.Empty;
            if (!Classes.TryGetValue(className, out var type)) continue;
            var sourceNamespace = Namespace(observation.GetProperty("Namespace").GetString() ?? string.Empty);
            var sourceRef = SourceId(sourceNamespace, className);
            sources.TryAdd(sourceRef, new(sourceRef, Origin(sourceNamespace, className), sourceNamespace, className, snapshot.ScannedAt, purpose));
            var fields = observation.GetProperty("Fields").EnumerateArray().Select(field => ReadField(field, sourceRef)).ToImmutableArray();
            if (className == "Win32_LogicalDisk" && fields.FirstOrDefault(x => x.Name == "DriveType") is { } driveType
                && TryGetInteger(driveType, out var driveCode) && driveCode == 4) type = FactObjectType.NetworkDisk;
            string Text(string name) => fields.FirstOrDefault(x => x.Name == name)?.Value is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
            var uniqueId = Text(type == FactObjectType.Partition ? "Guid" : "UniqueId");
            var objectId = Text("ObjectId");
            var rawIdentity = !string.IsNullOrWhiteSpace(uniqueId) ? uniqueId : objectId;
            if (rawIdentity.Length == 0 && !className.StartsWith("MSFT_", StringComparison.Ordinal)
                && observation.TryGetProperty("Identity", out var key)) rawIdentity = key.GetString() ?? "";
            if (type is FactObjectType.Computer or FactObjectType.OperatingSystem) rawIdentity = systemId.Value.ToString("N") + ":" + className;
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
            var reason = failure.TryGetProperty("ReasonCode", out var code) ? code.GetString() : "QueryFailed";
            var unsupported = className is "BatteryStaticData" or "BatteryStatus"
                && reason?.Contains("0x80041010", StringComparison.OrdinalIgnoreCase) == true;
            sources[id] = new(id, Origin(ns, className), ns, className, snapshot.ScannedAt, purpose,
                unsupported ? FieldReadState.Unavailable : FieldReadState.Failed, unsupported ? "UnsupportedClass" : reason);
        }
        var relationships = ImmutableArray.CreateBuilder<WinPoolFactRelationship>();
        var ids = objects.Select(x => x.Id).ToHashSet();
        void Link(string? from, string? to, string kind)
        {
            if (from is not null && to is not null && ids.Contains(from) && ids.Contains(to)) relationships.Add(new(from, to, kind, snapshot.ScannedAt));
        }
        IEnumerable<JsonElement> Rows(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];
        string RawText(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        string? Find(FactObjectType type, string unique, string objectId = "", string uniqueField = "UniqueId")
        {
            if (unique.Length == 0 && objectId.Length == 0) return null;
            var matches = objects.Where(x => x.ObjectType == type && (unique.Length > 0
                ? x.Field(uniqueField)?.DisplayValue() == unique : x.Field("ObjectId")?.DisplayValue() == objectId)).ToArray();
            return matches.Length == 1 ? matches[0].Id : null;
        }
        string? Key(FactObjectType type, string key) => key.StartsWith("uid:", StringComparison.Ordinal) ? Find(type, key[4..])
            : key.StartsWith("oid:", StringComparison.Ordinal) ? Find(type, "", key[4..]) : null;
        foreach (var pool in Rows("StoragePools"))
        {
            var id = Find(FactObjectType.StoragePool, RawText(pool, "UniqueId"), RawText(pool, "ObjectId"));
            Link(Key(FactObjectType.StorageSubsystem, RawText(pool, "SubsystemAssociationKey")), id, "subsystem-pool");
            if (pool.TryGetProperty("MemberPhysicalDiskKeys", out var members))
                foreach (var member in members.EnumerateArray()) Link(id, Key(FactObjectType.PhysicalDisk, member.GetString() ?? ""), "pool-member");
        }
        foreach (var disk in Rows("VirtualDisks"))
            Link(Key(FactObjectType.StoragePool, RawText(disk, "PoolAssociationKey")),
                Find(FactObjectType.VirtualDisk, RawText(disk, "UniqueId"), RawText(disk, "ObjectId")), "pool-virtual-disk");
        foreach (var disk in Rows("OsDisks"))
        {
            var id = Find(FactObjectType.Disk, RawText(disk, "UniqueId"), RawText(disk, "ObjectId"));
            Link(Key(FactObjectType.PhysicalDisk, RawText(disk, "PhysicalDiskAssociationKey")), id, "same-device");
            Link(Key(FactObjectType.VirtualDisk, RawText(disk, "VirtualDiskAssociationKey")), id, "same-device");
        }
        foreach (var partition in Rows("Partitions"))
        {
            var id = Find(FactObjectType.Partition, RawText(partition, "Guid"), RawText(partition, "ObjectId"), "Guid");
            Link(Find(FactObjectType.Disk, RawText(partition, "OsDiskUniqueId"), RawText(partition, "OsDiskObjectId")), id, "disk-partition");
            Link(id, Find(FactObjectType.Volume, RawText(partition, "VolumeUniqueId"), RawText(partition, "VolumeObjectId")), "partition-volume");
        }
        foreach (var tier in Rows("StorageTiers"))
        {
            var id = Find(FactObjectType.StorageTier, RawText(tier, "UniqueId"), RawText(tier, "ObjectId"));
            Link(Key(FactObjectType.StoragePool, RawText(tier, "PoolAssociationKey")), id, "pool-tier");
            Link(Key(FactObjectType.VirtualDisk, RawText(tier, "VirtualDiskAssociationKey")), id, "virtual-disk-tier");
        }
        // The mount path is an association observed in this capture, never a persistent volume identity.
        foreach (var logical in objects.Where(x => x.ObjectType == FactObjectType.LogicalDisk))
        {
            if (!StorageAccessPath.TryGetDriveLetter(logical.Field("DeviceID")?.DisplayValue(), out var letter)) continue;
            var matches = objects.Where(x => x.ObjectType == FactObjectType.Volume
                && StorageAccessPath.TryGetDriveLetter(x.Field("DriveLetter")?.DisplayValue(), out var mounted)
                && mounted == letter).ToArray();
            if (matches.Length == 1) Link(matches[0].Id, logical.Id, "same-volume");
        }
        foreach (var supplement in objects.Where(x => sources[x.SourceRef].ClassName is "Win32_DiskDrive" or "Windows.DiskRoles"))
        {
            var number = supplement.Field(sources[supplement.SourceRef].ClassName == "Win32_DiskDrive" ? "Index" : "DiskNumber");
            if (number is null || !TryGetInteger(number, out var index)) continue;
            var matches = objects.Where(x => x.ObjectType == FactObjectType.Disk && x.Field("Number") is { } n && TryGetInteger(n, out var value) && value == index).ToArray();
            if (matches.Length == 1) Link(matches[0].Id, supplement.Id, "disk-supplement");
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

    private static bool TryGetInteger(WinPoolSourceField field, out long value)
    {
        value = 0;
        if (field.ReadState != FieldReadState.Returned
            || field.Value is not { ValueKind: JsonValueKind.Number } number)
            return false;
        if (number.TryGetInt64(out value)) return true;
        if (!number.TryGetUInt64(out var unsigned) || unsigned > long.MaxValue) return false;
        value = (long)unsigned;
        return true;
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
