using WinPool.Core;

namespace WinPool.App.Services;

public static class DiskDetailFormatter
{
    public static string Format(StorageSnapshot snapshot, OsDiskInfo disk, bool zh)
    {
        var physical = snapshot.PhysicalDisks.FirstOrDefault(
            x => x.StableId == disk.PhysicalDiskStableId);
        var partitions = snapshot.Partitions
            .Where(x => x.OsDiskStableId == disk.StableId)
            .OrderBy(x => x.Offset)
            .ToList();

        string YesNo(bool value) => value ? (zh ? "是" : "Yes") : (zh ? "否" : "No");
        var lines = new List<string>
        {
            disk.FriendlyName,
            string.Empty,
            $"{(zh ? "磁盘 ID" : "Disk ID")} : {disk.StableId}",
            $"{(zh ? "类型" : "Type")}   : {(string.IsNullOrWhiteSpace(physical?.BusType) ? "—" : physical.BusType)}",
            $"{(zh ? "状态" : "Status")} : {(disk.IsOffline ? (zh ? "脱机" : "Offline") : (zh ? "联机" : "Online"))}",
            $"{(zh ? "分区形式" : "Partition style")} : {disk.PartitionStyle}",
            $"{(zh ? "当前只读状态" : "Current read-only state")}: {YesNo(false)}",
            $"{(zh ? "只读" : "Read-only")}: {YesNo(false)}",
            $"{(zh ? "启动磁盘" : "Boot disk")}: {YesNo(disk.IsBoot)}",
            $"{(zh ? "页面文件磁盘" : "Pagefile disk")}: {YesNo(physical?.IsPageFile == true)}",
            $"{(zh ? "休眠文件磁盘" : "Hibernation file disk")}: {YesNo(false)}",
            $"{(zh ? "故障转储磁盘" : "Crashdump disk")}: {YesNo(physical?.IsCrashDump == true)}",
            $"{(zh ? "群集磁盘" : "Clustered disk")}  : {YesNo(false)}",
            string.Empty
        };

        if (partitions.Count == 0)
        {
            lines.Add(zh ? "没有卷。" : "There are no volumes.");
        }
        else
        {
            lines.Add(zh
                ? "  卷 ###  盘符  标签        文件系统   大小"
                : "  Volume ###  Ltr  Label        Fs         Size");
            for (var i = 0; i < partitions.Count; i++)
            {
                var p = partitions[i];
                var label = string.IsNullOrWhiteSpace(p.FileSystemLabel) ? string.Empty : p.FileSystemLabel;
                var letter = string.IsNullOrWhiteSpace(p.DriveLetter) ? " " : p.DriveLetter;
                var fs = string.IsNullOrWhiteSpace(p.FileSystem) ? "RAW" : p.FileSystem;
                var volume = zh ? "卷" : "Volume";
                lines.Add(
                    $"  {volume} {i,-4}  {letter,-4}  {label,-10}  {fs,-9}  {TopologyProjector.FormatBytes(p.Size)}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}
