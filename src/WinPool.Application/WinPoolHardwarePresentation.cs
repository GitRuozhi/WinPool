namespace WinPool.Application;

public sealed record WinPoolHardwareSourceRow(WinPoolSource Source, int ObjectCount, DateTimeOffset? RetainedAt);

public static class WinPoolHardwarePresentation
{
    public static IReadOnlyList<ManagePropertyView> MarkUnavailable(StorageSnapshot snapshot, string objectId, IEnumerable<ManagePropertyView> rows) =>
        rows.Select(row =>
        {
            var field = row.PropertyTextKey switch { "Capacity" => "Size", "Available" => "SizeRemaining", "Allocated" => "AllocatedSize",
                "AllocationUnit" => "AllocationUnitSize", "PartitionTable" => "PartitionStyle", "RunningStatus" => "IsOffline", _ => row.PropertyTextKey };
            return snapshot.FieldIssues.Any(x => x.ObjectId == objectId && x.FieldName == field)
                || (field == "Size" && snapshot.Warnings.Any(x => x.StableId == objectId && x.Code == "facts.numeric-out-of-range"))
                ? row with { RawValue = "—", Presentation = ManageValuePresentation.Plain } : row;
        }).ToArray();

    public static IReadOnlyList<WinPoolHardwareSourceRow> SourceRows(WinPoolSystem system) => system.Sources
        .GroupBy(x => (x.Namespace, x.ClassName)).Select(group =>
        {
            var latest = group.MaxBy(x => x.CapturedAt)!;
            var observations = system.Objects.SelectMany(x => x.Sources).DistinctBy(x => x.Id)
                .Where(x => group.Any(s => s.Id == x.SourceRef)).ToArray();
            var retained = observations.Select(x => system.Sources.First(s => s.Id == x.SourceRef).CapturedAt)
                .Where(x => x < latest.CapturedAt).ToArray();
            return new WinPoolHardwareSourceRow(latest, observations.Length, retained.Length == 0 ? null : retained.Max());
        }).OrderBy(x => x.Source.ClassName, StringComparer.Ordinal).ToArray();

    public static string FieldName(string name, bool chinese) => name switch
    {
        "Size" => chinese ? "容量" : "Capacity",
        "SizeRemaining" or "FreeSpace" => chinese ? "可用空间" : "Free space",
        "AllocatedSize" => chinese ? "已分配空间" : "Allocated capacity",
        "FriendlyName" or "Name" => chinese ? "名称" : "Name",
        "FileSystem" => chinese ? "文件系统" : "File system",
        "FileSystemLabel" => chinese ? "卷标" : "Volume label",
        "AllocationUnitSize" => chinese ? "分配单元" : "Allocation unit",
        "HealthStatus" => chinese ? "健康状态" : "Health status",
        "OperationalStatus" => chinese ? "运行状态" : "Operational status",
        "SerialNumber" => chinese ? "序列号" : "Serial number",
        "Model" => chinese ? "型号" : "Model",
        "Manufacturer" => chinese ? "制造商" : "Manufacturer",
        "IsBoot" => chinese ? "启动角色" : "Boot role",
        "IsSystem" => chinese ? "系统角色" : "System role",
        "IsOffline" => chinese ? "脱机状态" : "Offline state",
        "Usage" => chinese ? "用途" : "Usage",
        "MediaType" => chinese ? "介质类型" : "Media type",
        "Interleave" => chinese ? "交错大小" : "Interleave",
        "NumberOfColumns" => chinese ? "列数" : "Column count",
        "NumberOfCores" => chinese ? "核心数" : "Core count",
        "NumberOfLogicalProcessors" => chinese ? "逻辑处理器数" : "Logical processor count",
        "Capacity" => chinese ? "容量" : "Capacity",
        "Speed" => chinese ? "速率" : "Speed",
        "DriveLetter" => chinese ? "盘符" : "Drive letter",
        "AccessPaths" => chinese ? "挂载路径" : "Mount paths",
        "Description" => chinese ? "描述" : "Description",
        _ => name
    };
}
