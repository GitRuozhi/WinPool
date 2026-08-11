using System.Globalization;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

internal static class HardwareReportFactory
{
    private static readonly HashSet<string> UnavailableItemIds =
    [
        "0915", // Storage-tier allocation unit is not exposed by Windows
        "1004", // Shared GPU memory requires a graphics API
        "1005", // Total GPU memory requires a graphics API
        "1006", // DirectX feature level requires a graphics API
        "1103", // Monitor-to-GPU mapping is not exposed read-only
        "1111", // Color format requires display configuration APIs
        "1112"  // Dynamic range requires display configuration APIs
    ];

    public static HardwareInventoryReport Create(
        StorageSnapshot snapshot,
        RawSnapshot raw,
        string diagnostics,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(raw);
        var hardware = raw.Hardware ?? new RawHardware();
        var values = BuildValues(snapshot, raw, hardware);
        var elapsed = checked((long)duration.TotalMilliseconds);

        var items = KsReferenceReportFactory.Create().Items.Select(definition =>
        {
            if (UnavailableItemIds.Contains(definition.Id))
            {
                return definition with
                {
                    FinalValue = null,
                    Sources =
                    [
                        new CollectorSourceResult(
                            "NativeCollector",
                            CollectorSourceStatus.Unavailable,
                            null,
                            "Windows does not expose this value through a read-only query.",
                            0)
                    ],
                    Warnings = ["This value cannot be collected through a read-only native collector."]
                };
            }

            var itemValues = values.TryGetValue(definition.Id, out var collected)
                ? collected
                : [];

            var hasData = itemValues.Any(value => !string.IsNullOrWhiteSpace(value));
            var element = JsonSerializer.SerializeToElement(itemValues);
            return new HardwareInventoryItemResult(
                definition.Id,
                definition.Category,
                definition.StandardName,
                definition.ChineseName,
                element,
                [
                    new CollectorSourceResult(
                        "WindowsPowerShell5.1",
                        hasData ? CollectorSourceStatus.Success : CollectorSourceStatus.NoData,
                        element,
                        string.Empty,
                        elapsed)
                ],
                hasData ? [] : ["The live query returned no data for this item."]);
        }).ToArray();

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            warnings.Add($"The inventory process wrote diagnostics: {diagnostics.Trim()}");
        }

        return new HardwareInventoryReport(1, snapshot.ScannedAt, items, warnings);
    }

    private static Dictionary<string, string[]> BuildValues(
        StorageSnapshot snapshot,
        RawSnapshot raw,
        RawHardware hardware)
    {
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddComputer(values, snapshot, hardware);
        AddSystem(values, snapshot, raw, hardware);
        AddMainboard(values, hardware);
        AddCpu(values, hardware);
        AddMemory(values, hardware);
        AddVirtualMemory(values, hardware);
        AddVolumes(values, raw);
        AddDisks(values, raw);
        AddStorage(values, raw);
        AddGpu(values, hardware);
        AddMonitors(values, hardware);
        AddNetwork(values, hardware);
        AddBattery(values, hardware);
        return values;
    }

    private static void AddComputer(
        Dictionary<string, string[]> values,
        StorageSnapshot snapshot,
        RawHardware hardware)
    {
        values["0101"] = [snapshot.Computer.Name];
        values["0102"] = [hardware.ComputerSystem.Manufacturer];
        values["0103"] = [hardware.ComputerSystem.Model];
        values["0104"] = [hardware.ComputerSystem.SystemType];
    }

    private static void AddSystem(
        Dictionary<string, string[]> values,
        StorageSnapshot snapshot,
        RawSnapshot raw,
        RawHardware hardware)
    {
        var os = hardware.OperatingSystem;
        values["0201"] = [os.Caption];
        values["0202"] = [First(os.DisplayVersion, os.ReleaseId)];
        values["0203"] = [os.BuildNumber];
        values["0204"] = [os.Activated ? "已激活" : "未激活"];
        values["0205"] = os.MUILanguages.Count > 0 ? [.. os.MUILanguages] : [os.InstalledUICulture];
        values["0206"] = [os.RegionName];
        values["0207"] = [First(os.TimeZoneCaption, os.TimeZoneStandardName)];
        values["0208"] = [hardware.ComputerSystem.UserName];
        values["0209"] = [YesNo(os.AdministratorDisabled)];
        values["0210"] = [os.PowerPlan];
        values["0211"] = [FormatDate(os.InstallDate)];
        values["0212"] = [FormatDate(raw.Computer.LastBootTime == default ? null : raw.Computer.LastBootTime.ToString("o"))];
        values["0213"] = [os.SystemDirectory];
        values["0214"] = [os.Processes > 0 ? os.Processes.ToString(CultureInfo.InvariantCulture) : string.Empty];
        values["0215"] = [os.Threads > 0 ? os.Threads.ToString(CultureInfo.InvariantCulture) : string.Empty];
        values["0216"] = [os.Handles > 0 ? os.Handles.ToString(CultureInfo.InvariantCulture) : string.Empty];
    }

    private static void AddMainboard(Dictionary<string, string[]> values, RawHardware hardware)
    {
        values["0301"] = [hardware.BaseBoard.Product];
        values["0302"] = [hardware.BaseBoard.Manufacturer];
        values["0303"] = [hardware.Bios.Mode];
        values["0304"] = [hardware.BaseBoard.SerialNumber];
        values["0305"] = [hardware.BaseBoard.Version];
        values["0306"] = [hardware.Bios.Version];
        values["0307"] = [hardware.Bios.Manufacturer];
    }

    private static void AddCpu(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var processors = hardware.Processors;
        values["0401"] = [.. processors.Select(x => x.Name)];
        values["0402"] = [.. processors.Select(x => FormatClock(x.MaxClockSpeed))];
        values["0403"] = [.. processors.Select(x => FormatNullable(x.NumberOfCores))];
        values["0404"] = [.. processors.Select(x => FormatNullable(x.NumberOfLogicalProcessors))];
        var l1 = hardware.CpuCaches
            .Where(x => x.Level == 3 && x.MaxCacheSize > 0)
            .Sum(x => x.MaxCacheSize!.Value);
        var l2 = hardware.CpuCaches
            .Where(x => x.Level == 4 && x.MaxCacheSize > 0)
            .Sum(x => x.MaxCacheSize!.Value);
        var l3 = hardware.CpuCaches
            .Where(x => x.Level == 5 && x.MaxCacheSize > 0)
            .Select(x => x.MaxCacheSize!.Value)
            .Distinct()
            .Sum(x => x);
        values["0405"] = [l1 > 0 ? $"{l1} KiB" : string.Empty];
        values["0406"] = l2 > 0
            ? [$"{l2} KiB"]
            : [.. processors.Select(x => x.L2CacheSize > 0 ? $"{x.L2CacheSize} KiB" : string.Empty)];
        values["0407"] = l3 > 0
            ? [$"{l3} KiB"]
            : [.. processors.Select(x => x.L3CacheSize > 0 ? $"{x.L3CacheSize} KiB" : string.Empty)];
    }

    private static void AddMemory(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var arrays = hardware.MemoryArrays;
        var devices = hardware.MemoryDevices;
        var slots = arrays.Sum(x => x.MemoryDevices ?? 0);
        values["0501"] = [slots > 0 ? slots.ToString(CultureInfo.InvariantCulture) : string.Empty];
        values["0502"] = devices.Count > 0
            ? [devices.Count.ToString(CultureInfo.InvariantCulture)]
            : [string.Empty];
        values["0503"] = [.. arrays.Select(x => MapErrorCorrection(x.MemoryErrorCorrection))];
        values["0504"] = [.. devices.Select(x => FormatBytes(x.Capacity))];
        values["0505"] = [.. devices.Select(x => x.Speed > 0 ? $"{x.Speed} MHz" : string.Empty)];
        values["0506"] = [.. devices.Select(x => MapMemoryType(x.SMBIOSMemoryType))];
        values["0507"] = [.. devices.Select(x => x.PartNumber)];
        values["0508"] = [.. devices.Select(x => x.DeviceLocator)];
        values["0509"] = [.. devices.Select(x => x.Manufacturer)];
        values["0510"] = [.. devices.Select(x => x.SerialNumber)];
    }

    private static void AddVirtualMemory(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var settings = hardware.PageFileSettings;
        values["0601"] = [.. settings.Select(x => FormatMegabytes(x.InitialSize))];
        values["0602"] = [.. settings.Select(x => FormatMegabytes(x.MaximumSize))];
        var locations = settings.Select(x => x.Name)
            .Concat(hardware.PageFileUsages.Select(x => x.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        values["0603"] = locations;
    }

    private static void AddVolumes(Dictionary<string, string[]> values, RawSnapshot raw)
    {
        var partitions = raw.Partitions;
        var logicalByDrive = raw.LogicalVolumes
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceID))
            .GroupBy(x => x.DeviceID.Trim().TrimEnd(':'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var networkByDrive = raw.NetworkDisks
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceId))
            .GroupBy(x => x.DeviceId.Trim().TrimEnd(':'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var styleByDisk = raw.OsDisks.ToDictionary(x => x.Number, x => x.PartitionStyle);

        RawLogicalVolume? LogicalFor(RawPartition partition)
        {
            var letter = TopologyProjector.NormalizeDriveLetter(partition.DriveLetter);
            return letter.Length == 0 ? null : logicalByDrive.GetValueOrDefault(letter);
        }

        string? NetworkPathFor(RawPartition partition)
        {
            var letter = TopologyProjector.NormalizeDriveLetter(partition.DriveLetter);
            return letter.Length == 0 ? null : networkByDrive.GetValueOrDefault(letter)?.ProviderName;
        }

        values["0701"] = [.. partitions.Select(x => TopologyProjector.NormalizeDriveLetter(x.DriveLetter))];
        values["0702"] = [.. partitions.Select(x => x.FileSystemLabel.Replace('\0', ' ').Trim())];
        values["0703"] = [.. partitions.Select(x => FormatBytes(x.Size))];
        values["0704"] = [.. partitions.Select(x => FormatBytes(x.SizeRemaining))];
        values["0705"] = [.. partitions.Select(x => FormatBytes(x.AllocationUnitSize))];
        values["0706"] = [.. partitions.Select(x => x.FileSystem)];
        values["0707"] = [.. partitions.Select(x => styleByDisk.GetValueOrDefault(x.DiskNumber) ?? string.Empty)];
        values["0708"] = [.. partitions.Select(x => First(x.Type, x.GptType, x.MbrType))];
        values["0709"] = [.. partitions.Select(x => string.IsNullOrWhiteSpace(x.DriveType) ? "None" : x.DriveType)];
        values["0718"] = [.. partitions.Select(x => LogicalFor(x)?.VolumeSerialNumber ?? string.Empty)];
        values["0710"] = [.. partitions.Select(x => YesNo(LogicalFor(x)?.Compressed ?? false))];
        values["0711"] = [.. partitions.Select(x => YesNo(x.IsBoot))];
        values["0712"] = [.. partitions.Select(x => YesNo(x.IsSystem))];
        values["0713"] = [.. partitions.Select(x => YesNo(x.IsHidden))];
        values["0717"] = [.. partitions.Select(x => x.OperationalStatus)];
        values["0714"] = [.. partitions.Select(x => x.DiskNumber.ToString(CultureInfo.InvariantCulture))];
        values["0716"] = [.. partitions.Select(x => FormatBytes(x.Offset))];
        values["0715"] = [.. partitions.Select(x => NetworkPathFor(x) ?? string.Empty)];
        values["0719"] = [.. partitions.Select(x => x.VolumeUniqueId)];
        values["0720"] = [.. partitions.Select(x => x.VolumeObjectId)];
    }

    private static void AddDisks(Dictionary<string, string[]> values, RawSnapshot raw)
    {
        var physicalDisks = raw.PhysicalDisks;
        var driveByNumber = raw.DiskDrives
            .Where(x => x.Index is not null)
            .GroupBy(x => x.Index!.Value)
            .ToDictionary(x => x.Key, x => x.First());
        var osDiskByPhysicalKey = raw.OsDisks
            .Where(x => !string.IsNullOrWhiteSpace(x.PhysicalDiskAssociationKey))
            .GroupBy(x => x.PhysicalDiskAssociationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var poolNameByKey = raw.StoragePools.ToDictionary(
            x => x.AssociationKey,
            x => x.FriendlyName,
            StringComparer.OrdinalIgnoreCase);

        RawOsDisk? OsDiskFor(RawPhysicalDisk disk) =>
            osDiskByPhysicalKey.GetValueOrDefault(disk.AssociationKey);

        RawDiskDrive? DriveFor(RawPhysicalDisk disk)
        {
            var osDisk = OsDiskFor(disk);
            if (osDisk is not null && driveByNumber.TryGetValue(osDisk.Number, out var byOsDisk))
            {
                return byOsDisk;
            }
            return disk.DeviceId is not null ? driveByNumber.GetValueOrDefault(disk.DeviceId.Value) : null;
        }

        var locations = physicalDisks.Select(x => ParsePhysicalLocation(x.PhysicalLocation)).ToArray();

        values["0801"] = [.. physicalDisks.Select(x => FormatNullable(x.DeviceId))];
        values["0829"] = [.. physicalDisks.Select(x => x.FriendlyName)];
        values["0802"] = [.. physicalDisks.Select(x => x.Model)];
        values["0825"] = [.. physicalDisks.Select(x => x.FirmwareVersion)];
        values["0803"] = [.. physicalDisks.Select(x => x.SerialNumber)];
        values["0804"] = [.. physicalDisks.Select(x => x.BusType)];
        values["0824"] = [.. physicalDisks.Select(x => DriveFor(x)?.InterfaceType ?? string.Empty)];
        values["0805"] = [.. physicalDisks.Select(x => FormatBytes(x.Size))];
        values["0806"] = [.. physicalDisks.Select(x => FormatBytes(x.LogicalSectorSize))];
        values["0807"] = [.. physicalDisks.Select(x => FormatBytes(x.PhysicalSectorSize))];
        values["0808"] = [.. physicalDisks.Select(x => x.MediaType)];
        values["0809"] = [.. physicalDisks.Select(x => OsDiskFor(x)?.PartitionStyle ?? string.Empty)];
        values["0810"] = [.. physicalDisks.Select(x => YesNo(OsDiskFor(x)?.IsBoot ?? x.IsBoot))];
        values["0811"] = [.. physicalDisks.Select(x => x.ProvisioningType)];
        values["0812"] = [.. physicalDisks.Select(x => FormatNullable(OsDiskFor(x)?.NumberOfPartitions))];
        values["0813"] = [.. physicalDisks.Select(x => x.HealthStatus)];
        values["0814"] = [.. physicalDisks.Select(x => x.OperationalStatus)];
        values["0815"] = [.. locations.Select(x => x.Prefix)];
        values["0816"] = [.. locations.Select(x => x.Bus)];
        values["0817"] = [.. locations.Select(x => x.Device)];
        values["0818"] = [.. locations.Select(x => x.Function)];
        values["0819"] = [.. locations.Select(x => x.Adapter)];
        values["0820"] = [.. locations.Select(x => x.Port)];
        values["0821"] = [.. locations.Select(x => x.Target)];
        values["0822"] = [.. locations.Select(x => x.Lun)];
        values["0823"] = [.. physicalDisks.Select(x =>
            poolNameByKey.GetValueOrDefault(x.PoolAssociationKey) ?? string.Empty)];
        values["0826"] = [.. physicalDisks.Select(x => x.UniqueId)];
        values["0827"] = [.. physicalDisks.Select(x => x.ObjectId)];
        values["0828"] = [.. physicalDisks.Select(x => OsDiskFor(x)?.Path ?? string.Empty)];
        values["0830"] = [.. physicalDisks.Select(x => x.PhysicalLocation)];
    }

    private static void AddStorage(Dictionary<string, string[]> values, RawSnapshot raw)
    {
        var pools = raw.StoragePools;
        var tiers = raw.StorageTiers;
        values["0901"] = [.. pools.Select(x => x.FriendlyName).Concat(tiers.Select(x => x.FriendlyName))];
        values["0902"] = [.. pools.Select(_ => "None").Concat(tiers.Select(x => x.MediaType))];
        values["0903"] = [.. pools.Select(x => FormatBytes(x.Size)).Concat(tiers.Select(x => FormatBytes(x.Size)))];
        values["0904"] = [.. pools.Select(x => FormatBytes(x.AllocatedSize))];
        values["0905"] = [.. tiers.Select(x => x.ResiliencySettingName)];
        values["0906"] = [.. pools.Select(x => FormatBytes(x.LogicalSectorSize))];
        values["0907"] = [.. pools.Select(x => FormatBytes(x.PhysicalSectorSize))];
        values["0908"] = [.. pools.Select(x => x.ProvisioningTypeDefault)];
        values["0909"] = [.. pools.Select(x => x.OperationalStatus)];
        values["0910"] = [.. pools.Select(x => x.HealthStatus)];
        values["0911"] = [.. tiers.Select(x => x.MediaType)];
        values["0912"] = [.. tiers.Select(x => x.ResiliencySettingName)];
        values["0913"] = [.. tiers.Select(x => FormatNullable(x.NumberOfColumns))];
        values["0914"] = [.. tiers.Select(x => FormatBytes(x.Interleave))];
        values["0916"] = [.. pools.Select(x => x.UniqueId)];
        values["0917"] = [.. pools.Select(x => x.ObjectId)];
        values["0918"] = [.. tiers.Select(x => x.UniqueId)];
        values["0919"] = [.. tiers.Select(x => x.ObjectId)];
        values["0920"] = [.. pools.Select(x => x.FriendlyName)];
        values["0921"] = [.. tiers.Select(x => x.FriendlyName)];
    }

    private static void AddGpu(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var controllers = hardware.VideoControllers;
        var locations = controllers.Select(x => ParseLocationNumbers(x.LocationInfo)).ToArray();
        values["1001"] = [.. controllers.Select(x => x.Name)];
        values["1002"] = [.. controllers.Select(x => x.DriverVersion)];
        values["1003"] = [.. controllers.Select(x => FormatBytes(x.DedicatedMemoryBytes))];
        values["1007"] = [.. locations.Select(x => x.Count > 0 ? x[0].ToString("00", CultureInfo.InvariantCulture) : string.Empty)];
        values["1008"] = [.. locations.Select(x => x.Count > 1 ? x[1].ToString("00", CultureInfo.InvariantCulture) : string.Empty)];
        values["1009"] = [.. locations.Select(x => x.Count > 2 ? x[2].ToString("00", CultureInfo.InvariantCulture) : string.Empty)];
    }

    private static void AddMonitors(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var monitors = hardware.Monitors;
        var refreshRates = hardware.VideoControllers
            .Select(x => x.CurrentRefreshRate)
            .FirstOrDefault(x => x is > 0);
        values["1101"] = [.. monitors.Select(x => First(x.UserFriendlyName, x.ProductCode))];
        values["1102"] = [.. monitors.Select(x => x.Manufacturer)];
        values["1104"] = [.. monitors.Select(x => FormatNullable(x.HorizontalPosition))];
        values["1105"] = [.. monitors.Select(x => FormatNullable(x.VerticalPosition))];
        values["1106"] = [.. monitors.Select(x => YesNo(x.Primary))];
        values["1107"] = [.. monitors.Select(x => FormatNullable(x.HorizontalResolution))];
        values["1108"] = [.. monitors.Select(x => FormatNullable(x.VerticalResolution))];
        values["1109"] = [.. monitors.Select(x =>
            x.Primary == true && refreshRates is > 0
                ? refreshRates.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty)];
        values["1110"] = [.. monitors.Select(x =>
            x.BitsPerPixel is > 0 ? $"{x.BitsPerPixel.Value / 4} bpc" : string.Empty)];
    }

    private static void AddNetwork(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var adapters = hardware.NetworkAdapters;
        values["1201"] = [.. adapters.Select(x => x.Name)];
        values["1202"] = [.. adapters.Select(x => x.InterfaceDescription)];
        values["1203"] = [.. adapters.Select(x => x.LinkSpeed)];
        values["1204"] = [.. adapters.Select(x => x.IPv4Addresses.FirstOrDefault() ?? string.Empty)];
        values["1205"] = [.. adapters.Select(x => x.IPv6Addresses.FirstOrDefault() ?? string.Empty)];
        values["1206"] = [.. adapters.Select(x => x.MacAddress)];
        values["1207"] = [.. adapters.Select(x => YesNo(x.DefaultGateways.Count > 0))];
        values["1208"] = [.. adapters.Select(x => YesNo(x.Dhcp.Equals("Enabled", StringComparison.OrdinalIgnoreCase)))];
        values["1209"] = [.. adapters.Select(x => x.DefaultGateways.FirstOrDefault() ?? string.Empty)];
        values["1210"] = [.. adapters.Select(x => string.Join(", ", x.DnsServers))];
        values["1211"] = [.. adapters.Select(x => x.Status)];
    }

    private static void AddBattery(Dictionary<string, string[]> values, RawHardware hardware)
    {
        var batteries = hardware.Batteries;
        values["1301"] = [(batteries.Count > 0).ToString()];
        values["1302"] = [.. batteries.Select(x =>
            x.DesignCapacity > 0 ? $"{x.DesignCapacity} mWh" : string.Empty)];
        values["1303"] = [.. batteries.Select(x =>
            x.RemainingCapacity > 0 ? $"{x.RemainingCapacity} mWh" : string.Empty)];
        values["1304"] = [.. batteries.Select(x => MapBatteryStatus(x.BatteryStatus))];
    }

    private static (string Prefix, string Bus, string Device, string Function, string Adapter, string Port, string Target, string Lun)
        ParsePhysicalLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var prefix = string.Empty;
        var colonIndex = location.IndexOf(':');
        if (colonIndex >= 0)
        {
            prefix = location[..colonIndex].Trim();
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tokens = location.Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bus", "Device", "Function", "Adapter", "Port", "Target", "LUN"
        };
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (names.Contains(tokens[index]) && long.TryParse(tokens[index + 1], out _))
            {
                fields[tokens[index]] = tokens[index + 1];
                index++;
            }
        }

        string Get(string name) => fields.GetValueOrDefault(name) ?? string.Empty;
        return (prefix, Get("Bus"), Get("Device"), Get("Function"), Get("Adapter"), Get("Port"), Get("Target"), Get("LUN"));
    }

    private static List<int> ParseLocationNumbers(string? locationInfo)
    {
        var numbers = new List<int>();
        if (string.IsNullOrWhiteSpace(locationInfo))
        {
            return numbers;
        }

        var current = -1;
        foreach (var character in locationInfo)
        {
            if (char.IsDigit(character))
            {
                current = current < 0 ? character - '0' : current * 10 + (character - '0');
            }
            else if (current >= 0)
            {
                numbers.Add(current);
                current = -1;
            }
        }
        if (current >= 0)
        {
            numbers.Add(current);
        }
        return numbers;
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return string.Empty;
        }

        string[] units = ["Byte", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return $"{bytes.Value} Byte";
        }

        var rounded = Math.Round(value, 2);
        return $"{rounded.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string FormatMegabytes(long? megabytes) =>
        megabytes is null ? string.Empty : FormatBytes(megabytes.Value * 1024L * 1024L);

    private static string FormatClock(long? megahertz)
    {
        if (megahertz is null or <= 0)
        {
            return string.Empty;
        }
        if (megahertz >= 1000)
        {
            var gigahertz = Math.Round(megahertz.Value / 1000.0, 1);
            return $"{gigahertz.ToString("0.#", CultureInfo.InvariantCulture)} GHz";
        }
        return $"{megahertz.Value} MHz";
    }

    private static string FormatNullable(long? value) =>
        value is null ? string.Empty : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string FormatNullable(int? value) =>
        value is null ? string.Empty : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string FormatDate(string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return string.Empty;
        }
        return DateTimeOffset.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : isoDate;
    }

    private static string YesNo(bool? value) =>
        value is null ? string.Empty : value.Value ? "是" : "否";

    private static string First(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string MapMemoryType(int? smbiosMemoryType) =>
        smbiosMemoryType switch
        {
            20 => "DDR",
            21 => "DDR2",
            22 => "DDR2 FB-DIMM",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => string.Empty
        };

    private static string MapErrorCorrection(int? correction) =>
        correction switch
        {
            3 => "None",
            4 => "Parity",
            5 => "Single-bit ECC",
            6 => "Multi-bit ECC",
            7 => "CRC",
            _ => string.Empty
        };

    private static string MapBatteryStatus(int? status) =>
        status switch
        {
            1 => "Discharging",
            2 => "AC Power",
            3 => "Fully Charged",
            4 => "Low",
            5 => "Critical",
            6 => "Charging",
            7 => "Charging and High",
            8 => "Charging and Low",
            9 => "Charging and Critical",
            10 => "Undefined",
            11 => "Partially Charged",
            _ => string.Empty
        };
}
