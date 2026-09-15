using System.Globalization;
using System.Reflection;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Rebuildable typed storage view. Values and associations come only from current source facts.</summary>
public static class WinPoolStorageProjection
{
    public static StorageSnapshot Project(WinPoolFacts facts)
    {
        facts.Validate();
        var unified = new WinPoolSystem(facts);
        var warnings = new List<InventoryWarning>();
        var fieldIssues = new List<StorageFieldIssue>();
        var objects = facts.Objects.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var sources = facts.Sources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        IEnumerable<WinPoolSourceObject> Of(FactObjectType type) => facts.Objects.Where(x => x.ObjectType == type);
        string[] Children(string id, string relation) => facts.Relationships.Where(x => x.FromId == id && x.Kind == relation).Select(x => x.ToId).Distinct().ToArray();
        string? Parent(string id, string relation)
        {
            var parents = facts.Relationships.Where(x => x.ToId == id && x.Kind == relation).Select(x => x.FromId).Distinct().ToArray();
            if (relation == "pool-member" && parents.Length > 1)
            {
                var ordinary = parents.Where(x => objects[x].Field("IsPrimordial")?.Value is { ValueKind: JsonValueKind.False }).ToArray();
                if (ordinary.Length == 1) return ordinary[0];
            }
            if (parents.Length > 1) warnings.Add(new("facts.ambiguous-relationship", "Multiple source relationships disagree.", id));
            return parents.Length == 1 ? parents[0] : null;
        }
        WinPoolSourceField? Field(WinPoolSourceObject item, string name) => item.Fields.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        T Build<T>(WinPoolSourceObject item, Dictionary<string, object?>? overrides = null)
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in Properties<T>())
            {
                if (overrides is not null && overrides.TryGetValue(property.Name, out var replacement))
                {
                    values[property.Name] = JsonSerializer.SerializeToElement(replacement); continue;
                }
                if (property.Name == "StableId") { values[property.Name] = JsonSerializer.SerializeToElement(item.Id); continue; }
                var name = property.Name switch { "DeviceIdentifier" => "DeviceId", _ => property.Name };
                var field = Field(item, name);
                if (field is null && item.ObjectType == FactObjectType.StorageTier && property.Name == "AllocatedSize")
                    field = Field(item, "FootprintOnPool");
                if (item.ObjectType == FactObjectType.PhysicalDisk || (item.ObjectType == FactObjectType.Disk && property.Name is "IsPageFile" or "IsCrashDump") || (item.ObjectType == FactObjectType.Partition && property.Name is "FileSystem" or "FileSystemLabel" or "AllocationUnitSize" or "SizeRemaining" or "HealthStatus" or "OperationalStatus" or "DriveLetter"))
                {
                    var selected = WinPoolSourceDetails.Select(unified.Resolve(item.Id)!, name);
                    field = selected.Value;
                    if (selected.HasConflict) fieldIssues.Add(new(item.Id, property.Name, FieldReadState.Returned, "SourceConflict"));
                }
                if (field is null && item.ObjectType == FactObjectType.NetworkDisk)
                    field = property.Name switch
                    {
                        "SizeRemaining" => Field(item, "FreeSpace"), "ProviderPath" => Field(item, "ProviderName"),
                        "DriveLetter" => Field(item, "DeviceID"), _ => null
                    };
                if (item.ObjectType == FactObjectType.Volume && property.Name is "FileSystem" or "FileSystemLabel" or "SizeRemaining" or "Size" )
                {
                    var logical = facts.Relationships.Where(r => r.Kind == "same-volume" && r.FromId == item.Id).Select(r => objects[r.ToId]).ToArray();
                    if (logical.Length == 1 && field is not { ReadState: FieldReadState.Returned, Value: { ValueKind: not JsonValueKind.Null } })
                        field = Field(logical[0], property.Name switch { "SizeRemaining" => "FreeSpace", "FileSystemLabel" => "VolumeName", _ => property.Name });
                }
                if (property.Name == "IsStable" && field is null)
                {
                    values[property.Name] = JsonSerializer.SerializeToElement(item.HasReliableIdentity); continue;
                }
                if (property.Name == "SizeSource" && field is null)
                {
                    values[property.Name] = JsonSerializer.SerializeToElement(sources[item.SourceRef].Origin == FactOrigin.Simulation
                        ? CapacitySourceKind.SimulatedEstimate : CapacitySourceKind.Collected); continue;
                }
                if (field is not { ReadState: FieldReadState.Returned })
                    fieldIssues.Add(new(item.Id, property.Name, field?.ReadState ?? FieldReadState.NotCollected, field?.ReasonCode));
                else if ((field.Value is null || field.Value is { ValueKind: JsonValueKind.Null })
                    && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                    fieldIssues.Add(new(item.Id, property.Name, FieldReadState.Unavailable, "ReturnedNull"));
                values[property.Name] = Convert(field, property.PropertyType, item.Id, warnings);
            }
            return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(values))!;
        }

        var computerSource = Of(FactObjectType.Computer).FirstOrDefault();
        var osSource = Of(FactObjectType.OperatingSystem).FirstOrDefault();
        var registry = facts.Objects.FirstOrDefault(x => sources[x.SourceRef].ClassName == "Registry.CurrentVersion");
        var computerOverrides = new Dictionary<string, object?>();
        void Context(string target, WinPoolSourceObject? source, string name)
        {
            var own = computerSource is null ? null : Field(computerSource, target);
            var value = own is { ReadState: FieldReadState.Returned, Value: { ValueKind: not JsonValueKind.Null } } ? own
                : source is null ? null : Field(source, name);
            if (value is { ReadState: FieldReadState.Returned, Value: { ValueKind: not JsonValueKind.Null } })
                computerOverrides[target] = target == "LastBootTime" && DateTimeOffset.TryParse(value.DisplayValue(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    ? time : value.DisplayValue();
        }
        Context("WindowsProductName", osSource, "Caption"); Context("WindowsVersion", osSource, "Version");
        Context("OsBuild", osSource, "BuildNumber"); Context("LastBootTime", osSource, "LastBootUpTime");
        Context("DisplayVersion", registry, "DisplayVersion"); Context("Ubr", registry, "UBR");
        var computer = computerSource is null
            ? new ComputerInfo(WinPoolIdentityRegistry.ScopedId(facts.SystemId, FactObjectType.Computer, "context"), "System", "", "", "", DateTimeOffset.MinValue)
            : Build<ComputerInfo>(computerSource, computerOverrides);
        var pools = Of(FactObjectType.StoragePool).Select(x => Build<StoragePoolInfo>(x, new()
        {
            ["SubsystemStableId"] = Parent(x.Id, "subsystem-pool"), ["MemberPhysicalDiskIds"] = Children(x.Id, "pool-member")
        })).ToArray();
        var osDisks = Of(FactObjectType.Disk).Select(x =>
        {
            var related = Parent(x.Id, "same-device");
            return Build<OsDiskInfo>(x, new()
            {
                ["PhysicalDiskStableId"] = related is not null && objects[related].ObjectType == FactObjectType.PhysicalDisk ? related : null,
                ["VirtualDiskStableId"] = related is not null && objects[related].ObjectType == FactObjectType.VirtualDisk ? related : null
            });
        }).ToArray();
        var physical = Of(FactObjectType.PhysicalDisk).Select(x =>
        {
            var view = Build<PhysicalDiskInfo>(x, new() { ["PoolStableId"] = Parent(x.Id, "pool-member") });
            var disk = osDisks.FirstOrDefault(d => d.PhysicalDiskStableId == x.Id);
            return disk is null ? view : view with
            {
                IsBoot = Field(x, "IsBoot") is null ? disk.IsBoot : view.IsBoot,
                IsSystem = Field(x, "IsSystem") is null ? disk.IsSystem : view.IsSystem
            };
        }).ToArray();
        var tiers = Of(FactObjectType.StorageTier).Select(x => Build<StorageTierInfo>(x, new()
        {
            ["PoolStableId"] = Parent(x.Id, "pool-tier"), ["VirtualDiskStableId"] = Parent(x.Id, "virtual-disk-tier"),
            ["MemberPhysicalDiskIds"] = Children(x.Id, "tier-member")
        })).ToArray();
        var virtualDisks = Of(FactObjectType.VirtualDisk).Select(x => Build<VirtualDiskInfo>(x, new()
        {
            ["PoolStableId"] = Parent(x.Id, "pool-virtual-disk"), ["TierStableIds"] = Children(x.Id, "virtual-disk-tier"),
            ["OsDiskNumbers"] = osDisks.Where(d => d.VirtualDiskStableId == x.Id).Select(d => d.Number).ToArray()
        })).ToArray();
        var partitions = Of(FactObjectType.Partition).Select(x =>
        {
            var partition = Build<PartitionInfo>(x, new() { ["OsDiskStableId"] = Parent(x.Id, "disk-partition") });
            if (sources[x.SourceRef].ClassName != "MSFT_Partition" || Field(x, "PartitionTypeId") is not null) return partition;
            var typeId = Field(x, "GptType")?.DisplayValue();
            var kind = typeId?.Trim('{', '}').ToLowerInvariant() switch
            {
                "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "MicrosoftReserved",
                "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EfiSystem",
                "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "WindowsRecovery",
                "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "BasicData",
                _ => partition.Type
            };
            if (kind is "BasicData" or "MicrosoftReserved" or "EfiSystem" or "WindowsRecovery")
                fieldIssues.RemoveAll(issue => issue.ObjectId == x.Id && issue.FieldName == "Type");
            if (!string.IsNullOrWhiteSpace(typeId)) fieldIssues.RemoveAll(issue => issue.ObjectId == x.Id && issue.FieldName == "PartitionTypeId");
            return partition with { Type = kind, PartitionTypeId = typeId ?? Field(x, "MbrType")?.DisplayValue() ?? "" };
        }).ToArray();
        var volumes = Of(FactObjectType.Volume).Select(x =>
        {
            var parent = Parent(x.Id, "partition-volume");
            if (parent is not null && unified.Resolve(x.Id)?.Id != parent) parent = null;
            var paths = Field(x, "AccessPaths") ?? (parent is null ? null : Field(objects[parent], "AccessPaths"));
            var pathsValue = Convert(paths, typeof(IReadOnlyList<string>), x.Id, warnings).Deserialize<string[]>() ?? [];
            if (pathsValue.Length == 0 && Field(x, "Path") is { ReadState: FieldReadState.Returned } path
                && path.DisplayValue() is { Length: > 0 } returnedPath) pathsValue = [returnedPath];
            if (pathsValue.Length == 0 && Field(x, "DriveLetter")?.DisplayValue() is { Length: > 0 } letter
                && StorageAccessPath.TryGetDriveLetter(letter, out var normalized)) pathsValue = [normalized + ":\\"];
            return Build<VolumeInfo>(x, new() { ["PartitionStableId"] = parent, ["AccessPaths"] = pathsValue, ["VolumeIdentity"] = x.Id });
        }).ToArray();
        var network = Of(FactObjectType.NetworkDisk).Where(x => Field(x, "DriveType") is not { } drive || drive.DisplayValue() == "4")
            .Select(x => Build<NetworkDiskInfo>(x)).ToArray();
        var snapshot = new StorageSnapshot(StorageSnapshot.CurrentSchemaVersion, facts.InventoryVersion, facts.InventoryCapturedAt, computer,
            Of(FactObjectType.StorageSubsystem).Select(x => Build<StorageSubsystemInfo>(x)).ToArray(), physical, pools, tiers,
            virtualDisks, osDisks, partitions, volumes, network, [], warnings);
        var rebuilt = StorageRelationshipProjector.Rebuild(snapshot with
        {
            FieldIssues = fieldIssues,
            UnknownTierMembershipPools = snapshot.StoragePools.Where(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Any(id =>
                objects.TryGetValue(id, out var disk) && sources[disk.SourceRef].Origin != FactOrigin.Simulation
                && !facts.Relationships.Any(r => r.Kind == "tier-member" && r.ToId == id)))
                .Select(x => x.StableId).ToArray()
        });
        foreach (var partition in rebuilt.Partitions)
        {
            var volume = rebuilt.VolumeForPartition(partition.StableId);
            if (volume is not null)
            {
                var shared = new[] { "FileSystem", "FileSystemLabel", "AllocationUnitSize", "SizeRemaining", "HealthStatus", "OperationalStatus", "Path", "DriveLetter" };
                fieldIssues.RemoveAll(x => x.ObjectId == partition.StableId && shared.Contains(x.FieldName) && x.Reason != "SourceConflict");
                foreach (var issue in fieldIssues.Where(x => x.ObjectId == volume.StableId && shared.Contains(x.FieldName)).ToArray())
                    fieldIssues.Add(issue with { ObjectId = partition.StableId });
            }
            else
            {
                // No filesystem source is legitimate for an unformatted/reserved partition.
                fieldIssues.RemoveAll(x => x.ObjectId == partition.StableId && x.State == FieldReadState.NotCollected
                    && x.FieldName is "FileSystem" or "FileSystemLabel" or "AllocationUnitSize" or "SizeRemaining" or "Path" or "HealthStatus");
            }
        }
        return rebuilt with
        {
            PartitionSourceIds = unified.Objects.OfType<WinPoolPartition>().SelectMany(x => x.Sources.Select(source => (source.Id, Union: x.Id)))
                .ToDictionary(x => x.Id, x => x.Union),
            UnattachedPartitions = unified.Objects.OfType<WinPoolPartition>().Where(x => x.Primary.ObjectType == FactObjectType.LogicalDisk)
                .Select(x =>
                {
                    string Text(string name) => x.Field(name) is { ReadState: FieldReadState.Returned, Value: { ValueKind: not JsonValueKind.Null } } f ? f.DisplayValue() : "";
                    long? Number(string name) => x.Field(name) is { } f && f.TryGetInt64(out var value) ? value : null;
                    var letter = Text("DriveLetter").TrimEnd(':');
                    return new StoragePartitionUnion(x.Id, x.Primary.HasReliableIdentity, string.IsNullOrEmpty(letter) ? "Unknown" : letter + ":",
                        null, "Unknown", null, Number("Size"), null, Text("FileSystem"), Text("FileSystemLabel"), letter, null,
                        Number("SizeRemaining"), "", "", string.IsNullOrEmpty(letter) ? [] : [letter + ":\\"], null, null, false, false,
                        x.Sources.Select(s => s.Id).ToArray());
                }).ToArray()
        };
    }

    private static PropertyInfo[] Properties<T>() => PropertyCache<T>.Value;
    private static class PropertyCache<T>
    {
        internal static readonly PropertyInfo[] Value = typeof(T).GetProperties().Where(x => x.SetMethod is not null).ToArray();
    }

    private static JsonElement Convert(WinPoolSourceField? field, Type declared, string objectId, List<InventoryWarning> warnings)
    {
        var nullable = Nullable.GetUnderlyingType(declared);
        var type = nullable ?? declared;
        object? fallback = nullable is not null ? null : type == typeof(string) ? ""
            : type == typeof(IReadOnlyList<string>) ? Array.Empty<string>()
            : type == typeof(IReadOnlyList<int>) ? Array.Empty<int>() : type.IsValueType ? Activator.CreateInstance(type) : null;
        if (field is not { ReadState: FieldReadState.Returned, Value: { } value }
            || value.ValueKind == JsonValueKind.Null)
            return JsonSerializer.SerializeToElement(fallback);
        if (type == typeof(string))
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? ""
                : value.ValueKind == JsonValueKind.Array ? string.Join("; ", value.EnumerateArray().Select(x => EnumText(field.Name, x)))
                : EnumText(field.Name, value);
            return JsonSerializer.SerializeToElement(text);
        }
        if (type == typeof(DateTimeOffset) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return JsonSerializer.SerializeToElement(date);
        if (type == typeof(bool) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.Clone();
        if (type == typeof(long) || type == typeof(int) || type.IsEnum)
        {
            long number;
            var valid = value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out number)
                : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
            if (valid && (type != typeof(int) || number is >= int.MinValue and <= int.MaxValue)) return JsonSerializer.SerializeToElement(number);
            if (value.ValueKind == JsonValueKind.Number) warnings.Add(new("facts.numeric-out-of-range", "Value exceeds the supported editing range; the original is retained in source details.", objectId));
        }
        if (value.ValueKind == JsonValueKind.Array && type == typeof(IReadOnlyList<string>))
            return JsonSerializer.SerializeToElement(value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()).ToArray());
        if (value.ValueKind == JsonValueKind.Array && type == typeof(IReadOnlyList<int>) && value.EnumerateArray().All(x => x.TryGetInt32(out _))) return value.Clone();
        return JsonSerializer.SerializeToElement(fallback);
    }

    private static string EnumText(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var code)) return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return name switch
        {
            "HealthStatus" => code switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => Number(code) },
            "OperationalStatus" => code switch { 0 => "Unknown", 1 => "Other", 2 => "OK", 3 => "Degraded", 6 => "Error", 10 => "Stopped", 12 => "NoContact", 13 => "LostCommunication", _ => Number(code) },
            "MediaType" => code switch { 0 => "Unspecified", 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => Number(code) },
            "Usage" => code switch { 0 => "Unknown", 1 => "AutoSelect", 2 => "ManualSelect", 3 => "HotSpare", 4 => "Retired", 5 => "Journal", _ => Number(code) },
            "CannotPoolReason" => code switch { 0 => "Unknown", 1 => "Other", 2 => "InPool", 3 => "NotHealthy", 4 => "RemovableMedia", 5 => "InUseByCluster", 6 => "Offline", 7 => "InsufficientCapacity", 8 => "SpareDisk", 9 => "ReservedBySubsystem", 10 => "Starting", 11 => "MicrosoftReservedPartition", 12 => "GptProtectivePartition", 13 => "ForeignDisk", 14 => "UnsupportedSectorSize", _ => Number(code) },
            "PartitionStyle" => code switch { 0 => "RAW", 1 => "MBR", 2 => "GPT", _ => Number(code) },
            "ProvisioningType" or "ProvisioningTypeDefault" => code switch { 1 => "Thin", 2 => "Fixed", _ => Number(code) },
            "BusType" => code switch { 0 => "Unknown", 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 14 => "Virtual", 15 => "FileBackedVirtual", 16 => "StorageSpaces", 17 => "NVMe", 18 => "SCM", _ => Number(code) },
            _ => Number(code)
        };
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
