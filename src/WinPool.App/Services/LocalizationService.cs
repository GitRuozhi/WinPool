using CommunityToolkit.Mvvm.ComponentModel;
using WinPool.Core;

namespace WinPool.App.Services;

public sealed class LocalizationService : ObservableObject
{
    private LanguagePreference _language = LanguagePreference.ZhCn;

    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Texts =
        new Dictionary<string, (string Zh, string En)>(StringComparer.Ordinal)
        {
            ["Manage"] = ("管理", "Manage"),
            ["Create"] = ("创建", "Create"),
            ["Test"] = ("测试", "Test"),
            ["Monitor"] = ("监控", "Monitor"),
            ["Development"] = ("开发", "Development"),
            ["Settings"] = ("设置", "Settings"),
            ["Simulation"] = ("模拟执行", "Simulation"),
            ["Real"] = ("真实执行", "Real execution"),
            ["SimulationShort"] = ("模拟", "Simulation"),
            ["RealShort"] = ("真实", "Real"),
            ["AdminRequired"] = ("点击后可选择以管理员身份重新启动 WinPool。", "Select to restart WinPool as administrator."),
            ["ElevationTitle"] = ("切换到真实执行", "Switch to real execution"),
            ["ElevationMessage"] = ("真实执行需要管理员权限。是否以管理员身份重新启动 WinPool？", "Real execution requires administrator privileges. Restart WinPool as administrator?"),
            ["RestartAsAdministrator"] = ("以管理员身份重启", "Restart as administrator"),
            ["Cancel"] = ("取消", "Cancel"),
            ["ElevationFailed"] = ("无法以管理员身份重新启动 WinPool。", "WinPool could not restart as administrator."),
            ["Warning"] = ("警告", "Warning"),
            ["Error"] = ("错误", "Error"),
            ["OperationFailed"] = ("操作失败。", "The operation failed."),
            ["InvalidPartitionPath"] = ("当前分区没有可打开的有效路径。", "The selected partition does not have a valid path to open."),
            ["System"] = ("系统", "System"),
            ["Pool"] = ("池", "Pools"),
            ["Tier"] = ("层", "Tiers"),
            ["Disk"] = ("磁盘", "Disks"),
            ["Partition"] = ("分区", "Partitions"),
            ["OperationArea"] = ("操作区", "Operations"),
            ["TopologyArea"] = ("完整存储拓扑", "Complete storage topology"),
            ["Scanning"] = ("正在扫描存储对象…", "Scanning storage objects…"),
            ["NeverModified"] = ("当前第一稿只读取系统信息，不会修改任何磁盘。", "This first draft is read-only and never modifies a disk."),
            ["Rescan"] = ("重新扫描", "Rescan"),
            ["CopySummary"] = ("复制摘要", "Copy summary"),
            ["CopyId"] = ("复制标识", "Copy ID"),
            ["Export"] = ("导出信息", "Export"),
            ["Open"] = ("在文件资源管理器中打开", "Open in File Explorer"),
            ["ViewRelated"] = ("查看关联对象", "View related"),
            ["NoSelection"] = ("当前分类没有可显示的对象。", "No object is available in this category."),
            ["AddStorageSystem"] = ("+ 添加其他存储系统", "+ Add another storage system"),
            ["LastScan"] = ("最后扫描", "Last scan"),
            ["Health"] = ("健康状态", "Health"),
            ["Type"] = ("类型", "Type"),
            ["Capacity"] = ("容量", "Capacity"),
            ["Allocated"] = ("已分配", "Allocated"),
            ["Available"] = ("可用", "Available"),
            ["AllocationUnit"] = ("簇大小", "Cluster size"),
            ["Model"] = ("型号", "Model"),
            ["Serial"] = ("序列号", "Serial"),
            ["Bus"] = ("总线", "Bus"),
            ["Media"] = ("介质", "Media"),
            ["CanPool"] = ("可池化", "Can pool"),
            ["CannotPoolReason"] = ("不可池化原因", "Cannot pool reason"),
            ["PoolOwner"] = ("所属池", "Pool"),
            ["FileSystem"] = ("文件系统", "File system"),
            ["Path"] = ("路径", "Path"),
            ["Role"] = ("角色/布局", "Role / layout"),
            ["Members"] = ("成员", "Members"),
            ["Objects"] = ("对象", "Objects"),
            ["Appearance"] = ("主题", "Theme"),
            ["AccentColor"] = ("主题色", "Accent color"),
            ["AccentDescription"] = ("跟随 Windows 强调色或选择预设颜色。", "Follow the Windows accent or choose a preset color."),
            ["SystemAccent"] = ("跟随 Windows", "Use Windows accent"),
            ["Blue"] = ("蓝色", "Blue"),
            ["Cyan"] = ("青色", "Cyan"),
            ["Green"] = ("绿色", "Green"),
            ["Purple"] = ("紫色", "Purple"),
            ["Orange"] = ("橙色", "Orange"),
            ["Red"] = ("红色", "Red"),
            ["SystemTheme"] = ("跟随系统", "Use system setting"),
            ["Light"] = ("亮色", "Light"),
            ["Dark"] = ("暗色", "Dark"),
            ["Language"] = ("语言", "Language"),
            ["Chinese"] = ("中文", "Chinese"),
            ["English"] = ("English", "English"),
            ["SettingsDescription"] = ("更改立即生效并保存。返回后会保留工作区状态。", "Changes apply immediately and are saved. Workspace state is preserved."),
            ["ScanFailed"] = ("扫描失败；已保留上一次成功结果。", "Scan failed; the last successful result is preserved."),
            ["Standard"] = ("标准用户", "Standard user"),
            ["Administrator"] = ("管理员", "Administrator"),
            ["PhysicalDisk"] = ("物理磁盘", "Physical disk"),
            ["VirtualDisk"] = ("虚拟磁盘", "Virtual disk"),
            ["NetworkDisk"] = ("网络磁盘", "Network disk"),
            ["OtherDisk"] = ("其他磁盘", "Other disk"),
            ["Network"] = ("网络", "Network"),
            ["Other"] = ("其他", "Other"),
            ["NetworkStorageGroup"] = ("网络存储组", "Network storage group"),
            ["OtherStorageGroup"] = ("其他存储组", "Other storage group"),
            ["OriginalPool"] = ("原始池", "Primordial pool"),
            ["StoragePool"] = ("存储池", "Storage pool"),
            ["StorageTier"] = ("存储层", "Storage tier"),
            ["PerformanceTier"] = ("性能层", "Performance tier"),
            ["CapacityTier"] = ("容量层", "Capacity tier"),
            ["Computer"] = ("当前计算机", "This computer"),
            ["SimulatedComputer"] = ("模拟系统", "Simulated system"),
            ["SimulationData"] = ("仿真数据", "Simulation data"),
            ["SimulatedComputerSubtitle"] = ("前端参考数据  与本机隔离", "Frontend reference data  isolated from this computer"),
            ["ExecutionMode"] = ("执行模式", "Execution mode"),
            ["ExecutionDescription"] = ("标准用户选择真实执行时，可确认以管理员身份重启。执行模式不会跨普通启动保存。", "Selecting Real as a standard user offers an administrator restart. Execution mode is not persisted across normal launches."),
            ["PrimaryPartition"] = ("主分区", "Primary partition"),
            ["ExtendedPartition"] = ("扩展分区", "Extended partition"),
            ["SimpleVolume"] = ("简单卷", "Simple volume"),
            ["SpannedVolume"] = ("跨区卷", "Spanned volume"),
            ["StripedVolume"] = ("带区卷", "Striped volume"),
            ["WindowsRecovery"] = ("Windows 恢复分区", "Windows recovery partition"),
            ["EfiSystem"] = ("EFI 系统分区", "EFI system partition"),
            ["MicrosoftReserved"] = ("Microsoft 保留分区 (MSR)", "Microsoft reserved partition (MSR)"),
            ["SystemReserved"] = ("系统保留分区", "System reserved partition"),
            ["UnknownPartition"] = ("未知分区类型", "Unknown partition type"),
            ["Unknown"] = ("未知", "Unknown"),
            ["Yes"] = ("是", "Yes"),
            ["No"] = ("否", "No"),
            ["Copied"] = ("已复制到剪贴板。", "Copied to clipboard."),
            ["Exported"] = ("信息已导出。", "Information exported."),
            ["ReadOnly"] = ("只读", "Read-only"),
            ["About"] = ("关于", "About"),
            ["AboutDescription"] = ("Windows Storage Spaces 只读管理预览版。", "Read-only Windows Storage Spaces management preview."),
            ["ProductName"] = ("产品名称", "Product name"),
            ["CurrentVersion"] = ("当前版本", "Current version"),
            ["Update"] = ("更新", "Updates"),
            ["UpdateDescription"] = ("更新信息将在系统默认浏览器中打开。", "Update information opens in your default browser."),
            ["UpdateSource"] = ("更新来源", "Update source"),
            ["UpdateMethod"] = ("更新方式", "Update method"),
            ["ExternalUpdate"] = ("外部网页", "External web page"),
            ["ViewUpdates"] = ("查看更新", "View updates"),
            ["OpenUpdateFailed"] = ("无法打开更新网页。", "The update page could not be opened.")
        };

    public LanguagePreference Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
            {
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public string this[string key] =>
        Texts.TryGetValue(key, out var pair)
            ? (Language == LanguagePreference.ZhCn ? pair.Zh : pair.En)
            : key;
}
