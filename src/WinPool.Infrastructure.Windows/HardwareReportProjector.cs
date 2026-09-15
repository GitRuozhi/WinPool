using System.Globalization;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public sealed record HardwareReportCell(string ObjectId, string Value, string Details);
public sealed record HardwareReportRow(string Label, IReadOnlyList<HardwareReportCell> Cells);
public sealed record HardwareReportSection(IReadOnlyList<HardwareReportRow> Rows);
public sealed record HardwareReportCategory(string Name, IReadOnlyList<HardwareReportSection> Sections);

public static class HardwareReportProjector
{
    public static IReadOnlyList<HardwareReportCategory> Project(StorageSystemDocument document, bool chinese)
    {
        if (document.Unified is not { } system) return [];
        string T(string zh, string en) => chinese ? zh : en;
        var categories = new List<HardwareReportCategory>();
        var computer = ByClass(system, "Win32_ComputerSystem").FirstOrDefault();
        var os = ByClass(system, "Win32_OperatingSystem").FirstOrDefault();
        var registry = ByClass(system, "Registry.CurrentVersion").FirstOrDefault();
        var licensing = ByClass(system, "SoftwareLicensingProduct").FirstOrDefault();
        var session = ByClass(system, "Windows.Session").FirstOrDefault();
        var platform = ByClass(system, "WinPool.Platform").FirstOrDefault();
        var board = ByClass(system, "Win32_BaseBoard").FirstOrDefault();
        var bios = ByClass(system, "Win32_BIOS").FirstOrDefault();

        categories.Add(Single(T("Computer", "Computer"),
            R(T("主机名", "Host name"), Cell(system, computer, "Name")),
            R(T("制造商", "Manufacturer"), Cell(system, computer, "Manufacturer")),
            R(T("型号", "Model"), Cell(system, computer, "Model")),
            R(T("架构", "Architecture"), Cell(system, computer, "SystemType"))));
        var build = Join(".", Text(os, "BuildNumber"), Text(registry, "UBR"));
        categories.Add(Single(T("System", "System"),
            R(T("名称", "Name"), Cell(system, os, "Caption")),
            R(T("版本", "Version"), Cell(system, registry, "DisplayVersion", "ReleaseId")),
            R(T("内部版本", "Build"), Literal(system, os ?? registry, build)),
            R(T("激活状态", "Activation"), BoolCode(system, licensing, "LicenseStatus", "1", T("已激活", "Activated"), T("未激活", "Not activated"))),
            R(T("语言", "Language"), Cell(system, os, "MUILanguages")),
            R(T("当前用户", "Current user"), Cell(system, computer, "UserName")),
            R(T("本地超级管理员", "Local Administrator"), Boolean(system, session, "AdministratorEnabled", T("启用", "Enabled"), T("禁用", "Disabled"))),
            R(T("电源计划", "Power plan"), Transform(system, session, "ActivePowerScheme", PowerPlan))));
        categories.Add(Single(T("Mainboard", "Mainboard"),
            R(T("型号", "Model"), Cell(system, board, "Product")),
            R(T("主板厂商", "Board manufacturer"), Cell(system, board, "Manufacturer")),
            R(T("BIOS 模式", "BIOS mode"), Cell(system, platform, "BiosMode")),
            R(T("BIOS 版本", "BIOS version"), Cell(system, bios, "SMBIOSBIOSVersion", "Version")),
            R(T("BIOS 厂商", "BIOS manufacturer"), Cell(system, bios, "Manufacturer"))));

        var processors = ByClass(system, "Win32_Processor");
        var caches = ByClass(system, "Win32_CacheMemory");
        categories.Add(Multi(T("CPU", "CPU"), processors,
            (T("型号", "Model"), p => Cell(system, p, "Name")),
            (T("基准速度", "Base speed"), p => Numeric(system, p, "MaxClockSpeed", " MHz")),
            (T("物理核心", "Physical cores"), p => Cell(system, p, "NumberOfCores")),
            (T("逻辑核心", "Logical cores"), p => Cell(system, p, "NumberOfLogicalProcessors")),
            (T("一级缓存", "L1 cache"), _ => processors.Count == 1
                ? Cache(system, caches, 3)
                : Missing("Cache ownership is unavailable for multiple processors.")),
            (T("二级缓存", "L2 cache"), p => Cell(system, p, "L2CacheSize", suffix: " KiB")),
            (T("三级缓存", "L3 cache"), p => Cell(system, p, "L3CacheSize", suffix: " KiB"))));

        var arrays = ByClass(system, "Win32_PhysicalMemoryArray");
        var modules = ByClass(system, "Win32_PhysicalMemory");
        var totalSlots = Sum(arrays, "MemoryDevices");
        var ecc = string.Join(", ", arrays.Select(x => MemoryEcc(Text(x, "MemoryErrorCorrection"))).Where(x => x.Length > 0).Distinct());
        categories.Add(new(T("Memory", "Memory"),
        [
            new([R(T("插槽总数", "Total slots"), Literal(system, arrays.FirstOrDefault(), totalSlots)),
                R(T("已使用插槽", "Used slots"), Literal(system, modules.FirstOrDefault(), modules.Count.ToString(CultureInfo.InvariantCulture))),
                R("ECC", Literal(system, arrays.FirstOrDefault(), ecc))]),
            Rows(modules,
                (T("容量", "Capacity"), x => Bytes(system, x, "Capacity")),
                (T("速度", "Speed"), x => Numeric(system, x, "ConfiguredClockSpeed", " MHz", "Speed")),
                (T("代际", "Generation"), x => Transform(system, x, "SMBIOSMemoryType", MemoryType)),
                (T("型号", "Model"), x => Cell(system, x, "PartNumber")))
        ]));

        var pageFiles = ByClass(system, "Win32_PageFileSetting");
        if (pageFiles.Count == 0) pageFiles = ByClass(system, "Win32_PageFileUsage");
        categories.Add(Multi(T("VirtualMemory", "Virtual memory"), pageFiles,
            (T("初始大小", "Initial size"), x => MegaBytes(system, x, "InitialSize", "AllocatedBaseSize")),
            (T("最大值", "Maximum size"), x => MegaBytes(system, x, "MaximumSize", "AllocatedBaseSize")),
            (T("页面文件位置", "Page file path"), x => Cell(system, x, "Name"))));

        var summary = ManageSystemSummaryProjector.Project(document);
        categories.Add(Single(T("Storage", "Storage"), summary.Select(x =>
            R(T(StorageZh(x.PropertyTextKey), StorageEn(x.PropertyTextKey)),
                new HardwareReportCell(document.Id, x.Presentation == ManageValuePresentation.LocalizationKey ? T("未知", "Unknown") : x.RawValue,
                    StorageDetails(system, x.PropertyTextKey, chinese)))).ToArray()));

        var gpus = ByClass(system, "WinPool.GraphicsAdapter");
        if (gpus.Count == 0) gpus = ByClass(system, "Win32_VideoController");
        categories.Add(Multi(T("GPU", "GPU"), gpus,
            (T("型号", "Model"), x => Cell(system, x, "Name")),
            (T("驱动", "Driver"), x => GpuFallback(system, x, "DriverVersion")),
            (T("专用显存", "Dedicated memory"), x => Bytes(system, x, "DedicatedVideoMemory", "DedicatedMemoryBytes", "AdapterRAM")),
            (T("共享显存", "Shared memory"), x => Bytes(system, x, "SharedSystemMemory")),
            (T("总计显存", "Total memory"), x => Bytes(system, x, "TotalVideoMemory")),
            ("DirectX", x => Cell(system, x, "DirectXFeatureLevel")),
            (T("总线", "Bus"), x => GpuLocation(system, x, 0)),
            (T("设备", "Device"), x => GpuLocation(system, x, 1)),
            (T("功能", "Function"), x => GpuLocation(system, x, 2))));

        var monitors = ByClass(system, "WinPool.GraphicsOutput");
        if (monitors.Count == 0) monitors = ByClass(system, "WmiMonitorID");
        categories.Add(Multi(T("Monitor", "Monitor"), monitors,
            (T("型号", "Model"), x => MonitorEdid(system, x, "UserFriendlyName", "Name")),
            (T("制造商", "Manufacturer"), x => MonitorEdid(system, x, "ManufacturerName")),
            (T("连接显卡", "Connected GPU"), x => Cell(system, x, "ConnectedAdapter")),
            (T("水平坐标", "Horizontal position"), x => Cell(system, x, "DesktopX")),
            (T("垂直坐标", "Vertical position"), x => Cell(system, x, "DesktopY")),
            (T("主显示器", "Primary monitor"), x => Boolean(system, x, "Primary", T("是", "Yes"), T("否", "No"))),
            (T("水平分辨率", "Horizontal resolution"), x => Cell(system, x, "HorizontalResolution")),
            (T("垂直分辨率", "Vertical resolution"), x => Cell(system, x, "VerticalResolution")),
            (T("刷新率", "Refresh rate"), x => Numeric(system, x, "RefreshRate", " Hz")),
            (T("位深", "Bit depth"), x => Numeric(system, x, "BitsPerColor", " bpc")),
            (T("颜色格式", "Color format"), x => Cell(system, x, "ColorSpace")),
            (T("动态范围", "Dynamic range"), x => Cell(system, x, "DynamicRange"))));

        var networks = ByClass(system, "WinPool.NetworkAdapter");
        if (networks.Count == 0) networks = ByClass(system, "MSFT_NetAdapter");
        categories.Add(Multi(T("Network", "Network"), networks,
            (T("名称", "Name"), x => Cell(system, x, "Name")),
            (T("硬件", "Hardware"), x => Cell(system, x, "InterfaceDescription", "DriverDescription")),
            (T("链路速度", "Link speed"), x => BitsPerSecond(system, x, "LinkSpeed", "ReceiveLinkSpeed")),
            ("IPv4", x => Cell(system, x, "IPv4Addresses")),
            ("IPv6", x => Cell(system, x, "IPv6Addresses")),
            ("MAC", x => Cell(system, x, "MacAddress", "PermanentAddress")),
            (T("主网络", "Primary network"), x => Boolean(system, x, "Primary", T("是", "Yes"), T("否", "No")))));
        return categories;
    }

    private static HardwareReportCategory Single(string name, params HardwareReportRow[] rows) => new(name, [new(rows)]);
    private static HardwareReportCategory Multi(string name, IReadOnlyList<WinPoolObject> objects,
        params (string Label, Func<WinPoolObject, HardwareReportCell> Cell)[] rows) => new(name, [Rows(objects, rows)]);
    private static HardwareReportSection Rows(IReadOnlyList<WinPoolObject> objects,
        params (string Label, Func<WinPoolObject, HardwareReportCell> Cell)[] rows) =>
        new(rows.Select(row => R(row.Label, objects.Count == 0 ? [Missing()] : objects.Select(row.Cell).ToArray())).ToArray());
    private static HardwareReportRow R(string label, params HardwareReportCell[] cells) => new(label, cells);
    private static HardwareReportCell Missing(string reason = "Not collected") => new(string.Empty, "—", reason);
    private static IReadOnlyList<WinPoolObject> ByClass(WinPoolSystem system, string className) => system.Objects
        .Where(x => x.Sources.Any(s => system.Sources.Any(source => source.Id == s.SourceRef && source.ClassName == className))).ToArray();
    private static string SourceClass(WinPoolSystem system, WinPoolObject item) =>
        system.Sources.First(x => x.Id == item.Primary.SourceRef).ClassName;

    private static HardwareReportCell Cell(WinPoolSystem system, WinPoolObject? item, params string[] names) => Cell(system, item, names, null);
    private static HardwareReportCell Cell(WinPoolSystem system, WinPoolObject? item, string name, string? fallback = null, string? suffix = null) =>
        Cell(system, item, fallback is null ? [name] : [name, fallback], suffix);
    private static HardwareReportCell Cell(WinPoolSystem system, WinPoolObject? item, string[] names, string? suffix)
    {
        if (item is null) return Missing();
        var field = names.Select(item.Field).FirstOrDefault(x => x is { ReadState: FieldReadState.Returned, Value: not null });
        var value = field is null ? "—" : Display(field);
        if (value != "—" && suffix is not null) value += suffix;
        return new(item.Id, value, WinPoolSourceDetails.Describe(system, item));
    }
    private static HardwareReportCell Literal(WinPoolSystem system, WinPoolObject? item, string value) => item is null
        ? new(string.Empty, value.Length == 0 ? "—" : value, "Derived value")
        : new(item.Id, value.Length == 0 ? "—" : value, WinPoolSourceDetails.Describe(system, item));
    private static HardwareReportCell Transform(WinPoolSystem system, WinPoolObject? item, string field, Func<string, string> transform)
    {
        var cell = Cell(system, item, field);
        return cell.Value == "—" ? cell : cell with { Value = transform(cell.Value) };
    }
    private static HardwareReportCell Boolean(WinPoolSystem system, WinPoolObject? item, string field, string yes, string no)
    {
        var cell = Cell(system, item, field);
        return cell.Value switch { "true" => cell with { Value = yes }, "false" => cell with { Value = no }, _ => cell };
    }
    private static HardwareReportCell BoolCode(WinPoolSystem system, WinPoolObject? item, string field, string trueCode, string yes, string no)
    {
        var cell = Cell(system, item, field);
        return cell.Value == "—" ? cell : cell with { Value = cell.Value == trueCode ? yes : no };
    }
    private static HardwareReportCell Numeric(WinPoolSystem system, WinPoolObject item, string field, string suffix, string? fallback = null) =>
        Cell(system, item, fallback is null ? [field] : [field, fallback], suffix);
    private static HardwareReportCell Bytes(WinPoolSystem system, WinPoolObject item, params string[] fields)
    {
        var cell = Cell(system, item, fields, null);
        var field = fields.Select(item.Field).FirstOrDefault(x => x is { ReadState: FieldReadState.Returned });
        return field is not null && TryUInt64(field, out var value) && value <= long.MaxValue
            ? cell with { Value = TopologyProjector.FormatBytes((long)value) } : cell;
    }
    private static HardwareReportCell MegaBytes(WinPoolSystem system, WinPoolObject item, params string[] fields)
    {
        var cell = Cell(system, item, fields, null);
        var field = fields.Select(item.Field).FirstOrDefault(x => x is { ReadState: FieldReadState.Returned });
        return field is not null && TryUInt64(field, out var value) && value <= long.MaxValue / 1024 / 1024
            ? cell with { Value = TopologyProjector.FormatBytes((long)value * 1024 * 1024) } : cell;
    }
    private static HardwareReportCell BitsPerSecond(WinPoolSystem system, WinPoolObject item, params string[] fields)
    {
        var cell = Cell(system, item, fields, null);
        var field = fields.Select(item.Field).FirstOrDefault(x => x is { ReadState: FieldReadState.Returned });
        if (field is null || !TryUInt64(field, out var value)) return cell;
        return cell with { Value = value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.##} Gbps" : $"{value / 1_000_000d:0.##} Mbps" };
    }
    private static HardwareReportCell Cache(WinPoolSystem system, IReadOnlyList<WinPoolObject> caches, long level)
    {
        var matching = caches.Where(x => Text(x, "Level") == level.ToString(CultureInfo.InvariantCulture)).ToArray();
        var total = Sum(matching, "MaxCacheSize");
        return Literal(system, matching.FirstOrDefault(), total.Length == 0 ? "" : total + " KiB");
    }
    private static HardwareReportCell GpuFallback(WinPoolSystem system, WinPoolObject gpu, string field)
    {
        var direct = Cell(system, gpu, field);
        if (direct.Value != "—") return direct;
        var fallback = MatchedVideoController(system, gpu);
        return Cell(system, fallback, field);
    }
    private static HardwareReportCell GpuLocation(WinPoolSystem system, WinPoolObject gpu, int part)
    {
        var controller = MatchedVideoController(system, gpu);
        var pnp = Text(controller, "PNPDeviceID");
        var registry = ByClass(system, "Registry.VideoMemory").FirstOrDefault(x => Text(x, "PNPDeviceID").Equals(pnp, StringComparison.OrdinalIgnoreCase));
        var numbers = Text(registry, "LocationInfo").Split([' ', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var n) ? (int?)n : null).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return Literal(system, registry ?? controller, numbers.Length > part ? numbers[part].ToString("00", CultureInfo.InvariantCulture) : string.Empty);
    }
    private static WinPoolObject? MatchedVideoController(WinPoolSystem system, WinPoolObject gpu)
    {
        if (!uint.TryParse(Text(gpu, "VendorId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vendor)
            || !uint.TryParse(Text(gpu, "DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var device)) return null;
        var signature = $"VEN_{vendor:X4}&DEV_{device:X4}";
        var matches = ByClass(system, "Win32_VideoController")
            .Where(x => Text(x, "PNPDeviceID").Contains(signature, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static string StorageDetails(WinPoolSystem system, string key, bool chinese)
    {
        var classes = key switch
        {
            "LocalStorage" or "PhysicalDisk" => new[] { "MSFT_PhysicalDisk" },
            "ExternalStorage" => ["Win32_LogicalDisk"],
            "StoragePool" => ["MSFT_StoragePool"],
            "VirtualDisk" => ["MSFT_VirtualDisk"],
            "Partition" => ["MSFT_Partition"],
            _ => ["MSFT_Volume", "Win32_LogicalDisk"]
        };
        var lines = classes.Select(className => system.Sources
            .Where(x => x.ClassName.Equals(className, StringComparison.Ordinal))
            .OrderByDescending(x => x.CapturedAt).FirstOrDefault())
            .Select(source => source is null
                ? (chinese ? "未采集" : "Not collected")
                : $"{source.ClassName}: {source.ReadState}, {source.CapturedAt.LocalDateTime:G}"
                    + (string.IsNullOrWhiteSpace(source.ReasonCode) ? "" : $", {source.ReasonCode}"));
        return (chinese ? "与管理页系统摘要共用同一投影。" : "Uses the same projection as the Manage system summary.")
            + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
    private static HardwareReportCell MonitorEdid(WinPoolSystem system, WinPoolObject monitor, string field, string? fallback = null)
    {
        var device = Text(monitor, "MonitorDeviceId");
        var hardwareId = device.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty;
        var wmi = ByClass(system, "WmiMonitorID").FirstOrDefault(x => hardwareId.Length > 0
            && Text(x, "InstanceName").Contains(hardwareId, StringComparison.OrdinalIgnoreCase));
        var owner = wmi ?? monitor;
        var names = fallback is null ? new[] { field } : new[] { field, fallback };
        var result = Cell(system, owner, names, null);
        var sourceField = names.Select(owner.Field).FirstOrDefault(x => x is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.Array } });
        return sourceField is null ? result : result with { Value = DecodeCodes(sourceField.Value!.Value.GetRawText()) };
    }
    private static string Text(WinPoolObject? item, string field) => item?.Field(field) is { ReadState: FieldReadState.Returned } value ? Display(value) : string.Empty;
    private static string Sum(IEnumerable<WinPoolObject> items, string field)
    {
        ulong total = 0; var any = false;
        foreach (var item in items)
            if (item.Field(field) is { ReadState: FieldReadState.Returned } value && TryUInt64(value, out var number)) { total += number; any = true; }
        return any ? total.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }
    private static bool TryUInt64(WinPoolSourceField field, out ulong value)
    {
        value = 0;
        return field.Value is { ValueKind: JsonValueKind.Number } json && json.TryGetUInt64(out value);
    }
    private static string Display(WinPoolSourceField field)
    {
        if (field.Value is not { } value) return "—";
        if (value.ValueKind == JsonValueKind.Array)
            return string.Join(", ", value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).Where(x => !string.IsNullOrWhiteSpace(x)));
        return field.DisplayValue();
    }
    private static string DecodeCodes(string value)
    {
        try
        {
            using var json = JsonDocument.Parse(value);
            return new string(json.RootElement.EnumerateArray().Select(x => (char)x.GetInt32()).Where(x => x != '\0').ToArray());
        }
        catch (JsonException) { return value; }
    }
    private static string Join(string separator, params string[] parts) => string.Join(separator, parts.Where(x => x.Length > 0));
    private static string PowerPlan(string value)
    {
        var close = value.LastIndexOf(')');
        var open = close < 0 ? -1 : value.LastIndexOf('(', close);
        return open >= 0 && close > open ? value[(open + 1)..close] : value;
    }
    private static string MemoryType(string value) => value switch { "20" => "DDR", "21" => "DDR2", "22" => "DDR2 FB-DIMM", "24" => "DDR3", "26" => "DDR4", "34" => "DDR5", _ => value };
    private static string MemoryEcc(string value) => value switch { "3" => "None", "4" => "Parity", "5" => "Single-bit ECC", "6" => "Multi-bit ECC", "7" => "CRC", _ => value };
    private static string StorageZh(string key) => key switch { "LocalStorage" => "本地存储容量", "ExternalStorage" => "外部存储容量", "StoragePool" => "存储池数", "PhysicalDisk" => "物理磁盘数", "VirtualDisk" => "虚拟磁盘数", "Partition" => "分区数", _ => "可访问卷数" };
    private static string StorageEn(string key) => key switch { "LocalStorage" => "Local storage capacity", "ExternalStorage" => "External storage capacity", "StoragePool" => "Storage pools", "PhysicalDisk" => "Physical disks", "VirtualDisk" => "Virtual disks", "Partition" => "Partitions", _ => "Accessible volumes" };
}
