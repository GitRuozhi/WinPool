namespace WinPool.Infrastructure.Windows;

internal sealed class RawHardware
{
    public RawComputerSystem ComputerSystem { get; set; } = new();
    public RawOperatingSystem OperatingSystem { get; set; } = new();
    public RawBios Bios { get; set; } = new();
    public RawBaseBoard BaseBoard { get; set; } = new();
    public List<RawProcessor> Processors { get; set; } = [];
    public List<RawCpuCache> CpuCaches { get; set; } = [];
    public List<RawMemoryArray> MemoryArrays { get; set; } = [];
    public List<RawMemoryDevice> MemoryDevices { get; set; } = [];
    public List<RawPageFileSetting> PageFileSettings { get; set; } = [];
    public List<RawPageFileUsage> PageFileUsages { get; set; } = [];
    public List<RawVideoController> VideoControllers { get; set; } = [];
    public List<RawMonitor> Monitors { get; set; } = [];
    public List<RawNetworkAdapter> NetworkAdapters { get; set; } = [];
    public List<RawBattery> Batteries { get; set; } = [];
}

internal sealed class RawComputerSystem
{
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SystemType { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
}

internal sealed class RawOperatingSystem
{
    public string Caption { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string ReleaseId { get; set; } = string.Empty;
    public string BuildNumber { get; set; } = string.Empty;
    public string UBR { get; set; } = string.Empty;
    public bool Activated { get; set; }
    public List<string> MUILanguages { get; set; } = [];
    public string InstalledUICulture { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string TimeZoneCaption { get; set; } = string.Empty;
    public string TimeZoneStandardName { get; set; } = string.Empty;
    public bool? AdministratorDisabled { get; set; }
    public string PowerPlan { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string SystemDirectory { get; set; } = string.Empty;
    public int Processes { get; set; }
    public long Threads { get; set; }
    public long Handles { get; set; }
}

internal sealed class RawBios
{
    public string Mode { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
}

internal sealed class RawBaseBoard
{
    public string Product { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

internal sealed class RawProcessor
{
    public string Name { get; set; } = string.Empty;
    public long? MaxClockSpeed { get; set; }
    public int? NumberOfCores { get; set; }
    public int? NumberOfLogicalProcessors { get; set; }
    public long? L2CacheSize { get; set; }
    public long? L3CacheSize { get; set; }
}

internal sealed class RawCpuCache
{
    public int? Level { get; set; }
    public long? MaxCacheSize { get; set; }
    public string Purpose { get; set; } = string.Empty;
}

internal sealed class RawMemoryArray
{
    public int? MemoryDevices { get; set; }
    public int? MemoryErrorCorrection { get; set; }
}

internal sealed class RawMemoryDevice
{
    public long? Capacity { get; set; }
    public long? Speed { get; set; }
    public long? ConfiguredClockSpeed { get; set; }
    public int? SMBIOSMemoryType { get; set; }
    public string PartNumber { get; set; } = string.Empty;
    public string DeviceLocator { get; set; } = string.Empty;
    public string BankLabel { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
}

internal sealed class RawPageFileSetting
{
    public string Name { get; set; } = string.Empty;
    public long? InitialSize { get; set; }
    public long? MaximumSize { get; set; }
}

internal sealed class RawPageFileUsage
{
    public string Name { get; set; } = string.Empty;
    public long? AllocBaseSize { get; set; }
    public long? CurrentUsage { get; set; }
}

internal sealed class RawVideoController
{
    public string Name { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public long? AdapterRAM { get; set; }
    public long? DedicatedMemoryBytes { get; set; }
    public int? CurrentHorizontalResolution { get; set; }
    public int? CurrentVerticalResolution { get; set; }
    public int? CurrentRefreshRate { get; set; }
    public int? CurrentBitsPerPixel { get; set; }
    public string PNPDeviceID { get; set; } = string.Empty;
    public string LocationInfo { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

internal sealed class RawMonitor
{
    public string Manufacturer { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string UserFriendlyName { get; set; } = string.Empty;
    public int? YearOfManufacture { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public int? HorizontalPosition { get; set; }
    public int? VerticalPosition { get; set; }
    public bool? Primary { get; set; }
    public int? HorizontalResolution { get; set; }
    public int? VerticalResolution { get; set; }
    public int? BitsPerPixel { get; set; }
}

internal sealed class RawNetworkAdapter
{
    public string Name { get; set; } = string.Empty;
    public string InterfaceDescription { get; set; } = string.Empty;
    public string LinkSpeed { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public List<string> IPv4Addresses { get; set; } = [];
    public List<string> IPv6Addresses { get; set; } = [];
    public string Dhcp { get; set; } = string.Empty;
    public List<string> DnsServers { get; set; } = [];
    public List<string> DefaultGateways { get; set; } = [];
}

internal sealed class RawBattery
{
    public string Name { get; set; } = string.Empty;
    public long? DesignCapacity { get; set; }
    public long? RemainingCapacity { get; set; }
    public int? BatteryStatus { get; set; }
    public int? EstimatedChargeRemaining { get; set; }
}
