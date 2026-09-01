using CommunityToolkit.Mvvm.ComponentModel;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

public sealed class LocalizationService : ObservableObject
{
    private LanguagePreference _language = LanguagePreference.SystemDefault;

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
            ["LocalRealOperations"] = ("本机真实操作", "Local real operations"),
            ["PreviewWarningTitle"] = ("测试版本", "Preview warning"),
            ["PreviewWarningMessage"] = (
                "本软件仍处于测试阶段。本机存储写操作尚未开放，所有本机修改按钮仍保持禁用。",
                "WinPool is still in preview. Local storage writes are not available and all local mutation commands remain disabled."),
            ["PreviewConfirmation"] = (
                "本软件仍处于测试阶段。勾选后不会开放任何本机存储写操作。是否继续？",
                "WinPool is still in preview. Enabling this option does not unlock any local storage writes. Continue?"),
            ["Confirm"] = ("确定", "Confirm"),
            ["AdminRequired"] = ("点击后可选择以管理员身份重新启动 WinPool。", "Select to restart WinPool as administrator."),
            ["ElevationTitle"] = ("切换到真实执行", "Switch to real execution"),
            ["ElevationMessage"] = ("真实执行需要管理员权限。是否以管理员身份重新启动 WinPool？", "Real execution requires administrator privileges. Restart WinPool as administrator?"),
            ["RestartAsAdministrator"] = ("以管理员身份重启", "Restart as administrator"),
            ["Cancel"] = ("取消", "Cancel"),
            ["Close"] = ("关闭", "Close"),
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
            ["Volume"] = ("卷", "Volumes"),
            ["UnallocatedLayer"] = ("未划层", "Unallocated"),
            ["VirtualDisks"] = ("虚拟磁盘", "Virtual disks"),
            ["OperationArea"] = ("操作区", "Operations"),
            ["TopologyArea"] = ("完整存储拓扑", "Complete storage topology"),
            ["Scanning"] = ("正在扫描存储对象…", "Scanning storage objects…"),
            ["ScanComplete"] = ("扫描完成", "Scan complete"),
            ["NeverModified"] = ("当前第一稿只读取系统信息，不会修改任何磁盘。", "This first draft is read-only and never modifies a disk."),
            ["Rescan"] = ("重新扫描", "Rescan"),
            ["CopySummary"] = ("复制摘要", "Copy summary"),
            ["CopyId"] = ("复制标识", "Copy ID"),
            ["Export"] = ("导出信息", "Export"),
            ["Import"] = ("导入", "Import"),
            ["Open"] = ("在文件资源管理器中打开", "Open in File Explorer"),
            ["ViewRelated"] = ("查看关联对象", "View related"),
            ["NoSelection"] = ("当前分类没有可显示的对象。", "No object is available in this category."),
            ["AddStorageSystem"] = ("+ 添加其他存储系统", "+ Add another storage system"),
            ["LastScan"] = ("最后扫描", "Last scan"),
            ["HostName"] = ("主机名", "Host name"),
            ["VersionNumber"] = ("版本号", "Version"),
            ["OsBuild"] = ("OS 内部版本", "OS build"),
            ["Cpu"] = ("中央处理器", "CPU"),
            ["Memory"] = ("内存", "Memory"),
            ["LocalStorage"] = ("本地存储", "Local storage"),
            ["ExternalStorage"] = ("外部存储", "External storage"),
            ["AccessibleVolumes"] = ("可访问卷", "Accessible volumes"),
            ["RunningStatus"] = ("运行状态", "Operational status"),
            ["ProvisioningType"] = ("预配类型", "Provisioning"),
            ["Resiliency"] = ("弹性", "Resiliency"),
            ["PhysicalSector"] = ("物理扇区", "Physical sector"),
            ["LogicalSector"] = ("逻辑扇区", "Logical sector"),
            ["DiskNumber"] = ("编号", "Number"),
            ["PartitionTable"] = ("分区表", "Partition style"),
            ["Firmware"] = ("固件", "Firmware"),
            ["InterfaceType"] = ("接口类型", "Interface"),
            ["OwningDisk"] = ("所属磁盘", "Disk"),
            ["SystemPartition"] = ("系统分区", "System partition"),
            ["HiddenPartition"] = ("隐藏分区", "Hidden partition"),
            ["PartitionStatus"] = ("分区状态", "Partition state"),
            ["StartOffset"] = ("起始偏移", "Offset"),
            ["DriveLetter"] = ("盘符", "Drive letter"),
            ["VolumeLabel"] = ("卷标", "Volume label"),
            ["Columns"] = ("列数", "Columns"),
            ["Interleave"] = ("条带大小", "Interleave"),
            ["FaultTolerance"] = ("容灾故障数", "Tolerated failures"),
            ["Offline"] = ("脱机", "Offline"),
            ["Online"] = ("联机", "Online"),
            ["Health"] = ("健康状态", "Health"),
            ["Type"] = ("类型", "Type"),
            ["Name"] = ("名称", "Name"),
            ["Capacity"] = ("容量", "Capacity"),
            ["Allocated"] = ("已分配", "Allocated"),
            ["Available"] = ("可用", "Available"),
            ["AllocationUnit"] = ("分配单元", "Allocation unit"),
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
            ["SystemLanguage"] = ("跟随系统", "System default"),
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
            ["ExecutionDescription"] = ("本机真实操作仍处于测试阶段。标准用户确认后可请求管理员重启；本机存储目前始终只读，勾选状态不会跨普通启动保存。", "Local real operations remain experimental. A standard user can request an administrator restart after confirming; local storage remains read-only, and the checked state is not persisted across normal launches."),
            ["PrimaryPartition"] = ("主分区", "Primary partition"),
            ["ExtendedPartition"] = ("扩展分区", "Extended partition"),
            ["SimpleVolume"] = ("简单卷", "Simple volume"),
            ["SpannedVolume"] = ("跨区卷", "Spanned volume"),
            ["StripedVolume"] = ("带区卷", "Striped volume"),
            ["WindowsRecovery"] = ("Windows 恢复分区", "Windows recovery partition"),
            ["EfiSystem"] = ("EFI 系统分区", "EFI system partition"),
            ["MicrosoftReserved"] = ("微软保留分区", "Microsoft Reserved Partition"),
            ["SystemReserved"] = ("系统保留分区", "System reserved partition"),
            ["UnknownPartition"] = ("未知分区类型", "Unknown partition type"),
            ["Unknown"] = ("未知", "Unknown"),
            ["Yes"] = ("是", "Yes"),
            ["No"] = ("否", "No"),
            ["Copied"] = ("已复制到剪贴板。", "Copied to clipboard."),
            ["Exported"] = ("信息已导出。", "Information exported."),
            ["ImportedSimulation"] = ("系统已导入为模拟副本。", "System imported as a simulation."),
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
            ["VisitWebsite"] = ("访问官网", "Visit website"),
            ["SendFeedback"] = ("提交反馈", "Send feedback"),
            ["OpenUpdateFailed"] = ("无法打开更新网页。", "The update page could not be opened."),
            ["Edit"] = ("编辑", "Edit"),
            ["Product"] = ("产品", "Product"),
            ["Version"] = ("版本", "Version"),
            ["Provider"] = ("提供方", "Provider"),
            ["Website"] = ("官网", "Website"),
            ["Feedback"] = ("反馈", "Feedback"),
            ["Community"] = ("轻松交流", "Community"),
            ["CommunityPending"] = ("QQ 群（暂未开放）", "QQ group (not available yet)"),
            ["Privacy"] = ("隐私", "Privacy"),
            ["ShowHardwareIds"] = ("显示硬件ID", "Show hardware IDs"),
            ["PrivacyWarningTitle"] = ("隐私警告", "Privacy warning"),
            ["PrivacyWarningMessage"] = (
                "开启后，界面将明文显示主板、内存、磁盘、卷序列号和 MAC 地址。这些信息可能被截图或共享时泄露。确定继续？",
                "When enabled, mainboard, memory, disk, and volume serial numbers and MAC addresses are shown in plain text and may leak through screenshots or sharing. Continue?"),
            ["InitializeDisk"] = ("初始化磁盘", "Disk initialization"),
            ["CreateMsrOnInitialize"] = ("初始化磁盘时创建微软保留分区", "Create a Microsoft Reserved Partition when initializing disks"),
            ["ExtendVolume"] = ("扩展卷", "Extend volume"),
            ["ShrinkVolume"] = ("压缩卷", "Shrink volume"),
            ["DeleteVolume"] = ("删除卷", "Delete volume"),
            ["NewPartition"] = ("新建分区", "New partition"),
            ["Format"] = ("格式化", "Format"),
            ["NewPool"] = ("新建池", "New pool"),
            ["CreateVirtualDisk"] = ("创建虚拟磁盘", "Create virtual disk"),
            ["Optimize"] = ("优化", "Optimize"),
            ["Trim"] = ("剪裁", "Trim"),
            ["Defragment"] = ("碎片整理", "Defragment"),
            ["MonitorIntro"] = ("实时显示本机磁盘的活动时间与读写速度。", "Shows live disk activity and read/write throughput of this computer."),
            ["SystemDisk"] = ("系统盘", "System disk"),
            ["Welcome"] = ("欢迎页面", "Welcome page"),
            ["WelcomeTitle"] = ("欢迎使用 WinPool ！", "Welcome to WinPool!"),
            ["WelcomeMessage"] = (
                "~~最好的~~ 开源免费的 Win 平台存储系统工具。\n原生 WinUI 应用，旨在替代 Windows 老旧的磁盘管理和存储空间的图形界面，支持win 原生软 Raid 和高性能分层存储池。",
                "~~The best~~ free and open-source storage system tool for the Win platform.\nA native WinUI application that aims to replace Windows' dated Disk Management and Storage Spaces graphical interfaces, supporting native Windows software RAID and high-performance tiered storage pools."),
            ["ShowWelcomeAtStart"] = ("启动时显示欢迎页面", "Show the welcome page at startup"),
            ["OpenWelcome"] = ("打开欢迎内容", "Open welcome"),
            ["WelcomeConfirm"] = ("我知道啦", "Got it"),
            ["Unhealthy"] = ("不健康", "Unhealthy"),
            ["DataLocation"] = ("数据存储位置", "Data location"),
            ["StandardLocation"] = ("标准位置", "Standard location"),
            ["PortableLocation"] = ("软件目录（便携）", "App folder (portable)"),
            ["DataLocationSwitched"] = ("数据存储位置已切换，已有数据已迁移。", "Data location changed; existing data was migrated."),
            ["DataLocationFailed"] = ("无法切换数据存储位置：目标目录不可写。", "The data location could not be changed: the target folder is not writable."),
            ["BackgroundMonitoring"] = ("后台持续监控", "Keep monitoring in background"),
            ["ContinuousMonitoring"] = ("持续监控", "Continuous monitoring"),
            ["MonitoringEvents"] = ("事件", "Events"),
            ["SamplingRate"] = ("采样频率", "Sampling rate"),
            ["RefreshRate"] = ("刷新率", "Refresh rate"),
            ["StartMonitoring"] = ("开始监控", "Start monitoring"),
            ["StopMonitoring"] = ("停止监控", "Stop monitoring"),
            ["ExportData"] = ("导出数据", "Export data"),
            ["Activity"] = ("活动", "Activity"),
            ["ReadSpeed"] = ("读取速度", "Read speed"),
            ["WriteSpeed"] = ("写入速度", "Write speed"),
            ["VolumeColumn"] = ("卷", "Volumes"),
            ["ColorColumn"] = ("图例", "Legend"),
            ["NoMonitoringData"] = ("还没有可导出的监控数据。", "There is no monitoring data to export yet."),
            ["ConvertedToSimulation"] = ("已把本机信息保存为模拟系统并激活。", "The local machine was saved as a simulation and activated."),
            ["TargetMissing"] = ("目标在本机上已不存在。", "The target no longer exists on this computer."),
            ["DeleteSimulation"] = ("删除模拟系统", "Delete simulation"),
            ["ConfirmDeleteSimulation"] = ("确定删除当前模拟系统？保存的模拟数据将被移除，此操作不可撤销。", "Delete the current simulation? The saved simulation data will be removed and cannot be recovered."),
            ["AutoColor"] = ("自动颜色", "Automatic color"),
            ["ActivityLabelShort"] = ("活", "A"),
            ["ReadLabelShort"] = ("读", "R"),
            ["WriteLabelShort"] = ("写", "W")
        };

    public LanguagePreference Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
            {
                OnPropertyChanged(nameof(EffectiveLanguage));
                OnPropertyChanged(nameof(IsChinese));
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public LanguagePreference EffectiveLanguage
    {
        get
        {
            if (Language != LanguagePreference.SystemDefault)
            {
                return Language;
            }

            return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? LanguagePreference.ZhCn
                : LanguagePreference.EnUs;
        }
    }

    public bool IsChinese => EffectiveLanguage == LanguagePreference.ZhCn;

    public string this[string key] =>
        Texts.TryGetValue(key, out var pair)
            ? (EffectiveLanguage == LanguagePreference.ZhCn ? pair.Zh : pair.En)
            : key;
}
