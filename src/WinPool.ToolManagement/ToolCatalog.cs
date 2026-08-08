using System.Collections.ObjectModel;
using WinPool.Application;

namespace WinPool.ToolManagement;

public static class KnownToolIds
{
    public static readonly ToolId DiskSpd = new("microsoft.diskspd");
    public static readonly ToolId Fio = new("fio");
    public static readonly ToolId RoboCopy = new("windows.robocopy");
    public static readonly ToolId RamMap = new("microsoft.sysinternals.rammap");
    public static readonly ToolId DiteFileGen = new("dite.filegen");
}

public enum ToolVersionProbeKind
{
    FileVersionMetadata
}

public sealed record ToolVersionPolicy(
    Version MinimumInclusive,
    Version MaximumExclusive)
{
    public ToolVersionSupportStatus Evaluate(string? versionText)
    {
        if (!ToolVersionParser.TryParse(versionText, out var version))
        {
            return ToolVersionSupportStatus.Unrecognized;
        }

        return version >= MinimumInclusive && version < MaximumExclusive
            ? ToolVersionSupportStatus.Supported
            : ToolVersionSupportStatus.Unsupported;
    }
}

public sealed record ToolDescriptor(
    ToolId Id,
    string DisplayName,
    string Purpose,
    IReadOnlyList<string> ExecutableFileNames,
    Uri OfficialHomePage,
    Uri OfficialInstallSource,
    ToolInstallerKind? InstallerKind,
    ToolVersionProbeKind VersionProbeKind,
    ToolVersionPolicy SupportedVersions,
    ToolCapabilities Capabilities,
    bool RequiresElevationForUse,
    bool RequiresElevationForInstall,
    string? OfficialPackageSha256,
    bool AllowMissingVersionMetadata = false);

public sealed class ToolCatalog
{
    private static readonly IReadOnlyList<ToolDescriptor> BuiltInDescriptors =
        new ReadOnlyCollection<ToolDescriptor>(
        [
            new(
                KnownToolIds.DiskSpd,
                "DiskSpd",
                "文件型顺序、随机和混合 I/O 基准测试",
                new ReadOnlyCollection<string>(["diskspd.exe"]),
                new Uri("https://github.com/microsoft/diskspd"),
                new Uri("https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.zip"),
                ToolInstallerKind.PortableArchive,
                ToolVersionProbeKind.FileVersionMetadata,
                new ToolVersionPolicy(new Version(2, 2), new Version(2, 3)),
                ToolCapabilities.SequentialIo
                    | ToolCapabilities.RandomIo
                    | ToolCapabilities.MixedIo
                    | ToolCapabilities.FileGeneration
                    | ToolCapabilities.LatencyMetrics
                    | ToolCapabilities.StructuredOutput,
                false,
                false,
                null),
            new(
                KnownToolIds.Fio,
                "fio",
                "可配置文件 I/O 工作负载和 JSON 结果",
                new ReadOnlyCollection<string>(["fio.exe"]),
                new Uri("https://github.com/axboe/fio"),
                new Uri("https://github.com/axboe/fio/releases/download/fio-3.42/fio-3.42-x64.msi"),
                ToolInstallerKind.Msi,
                ToolVersionProbeKind.FileVersionMetadata,
                new ToolVersionPolicy(new Version(3, 31), new Version(4, 0)),
                ToolCapabilities.SequentialIo
                    | ToolCapabilities.RandomIo
                    | ToolCapabilities.MixedIo
                    | ToolCapabilities.FileGeneration
                    | ToolCapabilities.FileVerification
                    | ToolCapabilities.LatencyMetrics
                    | ToolCapabilities.StructuredOutput,
                false,
                true,
                "D6BC1C0EB7A4B3BD2810E6C0CE605917A4671CC126C9DAE5BE7EB4891464A5C6"),
            new(
                KnownToolIds.DiteFileGen,
                "Dite FileGen",
                "过渡期大文件/混合文件外部生成器",
                new ReadOnlyCollection<string>(["Dite.exe"]),
                new Uri("https://github.com/GitRuozhi/DiTe"),
                new Uri("https://github.com/GitRuozhi/DiTe/releases"),
                null,
                ToolVersionProbeKind.FileVersionMetadata,
                new ToolVersionPolicy(new Version(24, 0), new Version(25, 0)),
                ToolCapabilities.FileGeneration
                    | ToolCapabilities.StructuredOutput,
                false,
                false,
                null,
                AllowMissingVersionMetadata: true),
            new(
                KnownToolIds.RoboCopy,
                "RoboCopy",
                "Windows 文件复制、元数据复制和恢复语义",
                new ReadOnlyCollection<string>(["robocopy.exe"]),
                new Uri("https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy"),
                new Uri("https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy"),
                null,
                ToolVersionProbeKind.FileVersionMetadata,
                new ToolVersionPolicy(new Version(10, 0), new Version(11, 0)),
                ToolCapabilities.FileCopy | ToolCapabilities.FileVerification,
                false,
                false,
                null),
            new(
                KnownToolIds.RamMap,
                "RAMMap",
                "类型化的系统文件缓存和 standby list 清理",
                new ReadOnlyCollection<string>(["RAMMap.exe", "RAMMap64.exe", "RAMMap64a.exe"]),
                new Uri("https://learn.microsoft.com/sysinternals/downloads/rammap"),
                new Uri("https://download.sysinternals.com/files/RAMMap.zip"),
                ToolInstallerKind.PortableArchive,
                ToolVersionProbeKind.FileVersionMetadata,
                new ToolVersionPolicy(new Version(1, 61), new Version(2, 0)),
                ToolCapabilities.SystemCacheCleanup,
                true,
                false,
                null)
        ]);

    private readonly IReadOnlyDictionary<ToolId, ToolDescriptor> descriptors;

    public ToolCatalog(IReadOnlyList<ToolDescriptor>? registeredDescriptors = null)
    {
        var source = registeredDescriptors ?? BuiltInDescriptors;
        descriptors = source.ToDictionary(descriptor => descriptor.Id);
        ListedDescriptors = new ReadOnlyCollection<ToolDescriptor>(source.ToArray());
    }

    private IReadOnlyList<ToolDescriptor> ListedDescriptors { get; }

    public IReadOnlyList<ToolDescriptor> List() => ListedDescriptors;

    public bool TryGet(ToolId id, out ToolDescriptor descriptor) =>
        descriptors.TryGetValue(id, out descriptor!);
}
