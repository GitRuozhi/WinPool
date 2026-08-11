using WinPool.Application;

namespace WinPool.App.Services;

/// <summary>
/// Isolated, immutable inventory used to exercise the UI without mapping any
/// simulated object to a local disk command or mount point.
/// </summary>
public static class SimulationStorageSnapshotFactory
{
    private const string ComputerId = "simulation:system:winpool-v01";

    public const string SimulatedComputerName = "DESKTOP-PL96UKD";

    public const string SimulatedSnapshotVersion = "simulation-winpool-v02";

    public static StorageSnapshot Create()
    {
        var disks = new[]
        {
            Disk("sim:disk:00", 0, "ST8000NM017B-2TJ103", "ST8000NM017B-2TJ103", "HDD", 8001563222016, "sim:pool:primordial",
                bus: "SATA", interfaceType: "IDE", firmware: "SNA1", provisioning: "Fixed"),
            Disk("sim:disk:01", 1, "WDC WD42EJRX-89BFNY0", "WDC WD42EJRX-89BFNY0", "HDD", 4000787030016, "sim:pool:primordial",
                bus: "SATA", interfaceType: "IDE", firmware: "80.00A80", provisioning: "Fixed"),
            Disk("sim:disk:02", 2, "Samsung SSD 860 EVO 500GB", "Samsung SSD 860 EVO 500GB", "SSD", 500107862016, "sim:pool:02",
                bus: "SATA", physicalSector: 512),
            Disk("sim:disk:03", 3, "FORESEE 256GB SSD", "FORESEE 256GB SSD", "SSD", 256060514304, "sim:pool:01",
                bus: "SATA", physicalSector: 512),
            Disk("sim:disk:04", 4, "ZHITAI TiPro7000 THREE-BODY 2TB", "ZHITAI TiPro7000 THREE-BODY 2TB", "SSD", 2048408248320, "sim:pool:primordial",
                isSystem: true, bus: "NVMe", interfaceType: "SCSI", firmware: "ZTA32F46", provisioning: "Fixed"),
            Disk("sim:disk:05", 5, "Predator SSD GM7 M.2 4TB", "Predator SSD GM7 M.2 4TB", "SSD", 4096805658624, "sim:pool:primordial",
                bus: "NVMe", interfaceType: "SCSI", firmware: "SN12717", provisioning: "Fixed"),
            Disk("sim:disk:06", 6, "SEAGATE ST3000NM0023 SCSI Disk Device", "ST3000NM0023", "HDD", 3000592982016, "sim:pool:01",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:07", 7, "SEAGATE ST3000NM0023 SCSI Disk Device", "ST3000NM0023", "HDD", 3000592982016, "sim:pool:01",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:08", 8, "SEAGATE ST3000NM0023 SCSI Disk Device", "ST3000NM0023", "HDD", 3000592982016, "sim:pool:01",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:09", 9, "SEAGATE ST4000NM0023 SCSI Disk Device", "ST4000NM0023", "HDD", 4000787030016, "sim:pool:02",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:10", 10, "SEAGATE ST4000NM0023 SCSI Disk Device", "ST4000NM0023", "HDD", 4000787030016, "sim:pool:02",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:11", 11, "SEAGATE ST4000NM0025 SCSI Disk Device", "ST4000NM0025", "HDD", 4000787030016, "sim:pool:02",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:12", 12, "SEAGATE ST4000NM0025 SCSI Disk Device", "ST4000NM0025", "HDD", 4000787030016, "sim:pool:02",
                bus: "SAS", physicalSector: 512),
            Disk("sim:disk:13", 13, "HITACHI HUS723030ALS640 SCSI Disk Device", "HUS723030ALS640", "HDD", 3000592982016, "sim:pool:01",
                bus: "SAS", physicalSector: 512)
        };

        var pools = new[]
        {
            new StoragePoolInfo(
                "sim:pool:primordial", true, "Primordial", true, "Healthy", "OK",
                46909252583424, 28760329945088,
                "sim:subsystem:spaces",
                disks.Where(x => x.PoolStableId == "sim:pool:primordial").Select(x => x.StableId).ToArray()),
            new StoragePoolInfo(
                "sim:pool:01", true, "Pool01", false, "Healthy", "OK",
                12255069470720, 12244146454528, "sim:subsystem:spaces",
                disks.Where(x => x.PoolStableId == "sim:pool:01").Select(x => x.StableId).ToArray()),
            new StoragePoolInfo(
                "sim:pool:02", true, "Pool02", false, "Healthy", "OK",
                16499891765248, 16332150013952, "sim:subsystem:spaces",
                disks.Where(x => x.PoolStableId == "sim:pool:02").Select(x => x.StableId).ToArray())
        };

        var tiers = new[]
        {
            Tier("sim:tier:pool01-ssd", "Pool01-SSD", "SSD", "Simple", 249644974080, "sim:pool:01", "sim:vdisk:pool01", ["sim:disk:03"], 1),
            Tier("sim:tier:pool01-hdd", "Pool01-HDD", "HDD", "Parity", 8992050905088, "sim:pool:01", "sim:vdisk:pool01",
                ["sim:disk:06", "sim:disk:07", "sim:disk:08", "sim:disk:13"], 4),
            Tier("sim:tier:pool02-ssd", "Pool02-SSDTier02", "SSD", "Simple", 493921239040, "sim:pool:02", "sim:vdisk:pool02", ["sim:disk:02"], 1),
            Tier("sim:tier:pool02-hdd", "Pool02-HDDTier02", "HDD", "Parity", 10555419000832, "sim:pool:02", "sim:vdisk:pool02",
                ["sim:disk:09", "sim:disk:10", "sim:disk:11", "sim:disk:12"], 4)
        };

        var virtualDisks = new[]
        {
            new VirtualDiskInfo(
                "sim:vdisk:pool01", true, "Pool01", "Healthy", "OK", "Tiered", "Fixed",
                3, 64 * 1024, 9241695879168, 12242804277248, "sim:pool:01",
                ["sim:tier:pool01-ssd", "sim:tier:pool01-hdd"], [15]),
            new VirtualDiskInfo(
                "sim:vdisk:pool02", true, "Pool02", "Healthy", "OK", "Tiered", "Fixed",
                3, 64 * 1024, 11049340239872, 16330807836672, "sim:pool:02",
                ["sim:tier:pool02-ssd", "sim:tier:pool02-hdd"], [14])
        };

        var osDisks = new[]
        {
            OsDisk(0, "ST8000NM017B-2TJ103", 8001563222016, physicalId: "sim:disk:00"),
            OsDisk(1, "WDC WD42EJRX-89BFNY0", 4000787030016, physicalId: "sim:disk:01"),
            OsDisk(4, "ZHITAI TiPro7000 THREE-BODY 2TB", 2048408248320, physicalId: "sim:disk:04", isSystem: true),
            OsDisk(5, "Predator SSD GM7 M.2 4TB", 4096805658624, physicalId: "sim:disk:05"),
            OsDisk(14, "Pool02", 11049340239872, virtualId: "sim:vdisk:pool02"),
            OsDisk(15, "Pool01", 9241695879168, virtualId: "sim:vdisk:pool01")
        };

        var partitions = new[]
        {
            Partition(0, 1, 16759808, null, "", type: "MicrosoftReserved", fileSystem: "", offset: 17408, remaining: 0, isHidden: true, operationalStatus: "Other"),
            Partition(0, 2, 8001545043968, "G", "希捷企业", offset: 16777216, remaining: 1414682701824),
            Partition(1, 1, 16759808, null, "", type: "MicrosoftReserved", fileSystem: "", offset: 17408, remaining: 0, isHidden: true, operationalStatus: "Other"),
            Partition(1, 2, 4000768327680, "F", "西数监控", offset: 16777216, remaining: 946147430400),
            Partition(4, 1, 104857600, null, "", isSystem: true, type: "EfiSystem", fileSystem: "", offset: 1048576, remaining: 0, isHidden: true, operationalStatus: "Other"),
            Partition(4, 2, 16777216, null, "", type: "MicrosoftReserved", fileSystem: "", offset: 105906176, remaining: 0, isHidden: true, operationalStatus: "Other"),
            Partition(4, 3, 549756751872, "C", "", isBoot: true, offset: 122683392, remaining: Gib(221.179)),
            Partition(4, 4, 1497990430720, "D", "本地磁盘", offset: 549879545856, remaining: Gib(356.88)),
            Partition(4, 5, 537919488, null, "Recovery", type: "WindowsRecovery", fileSystem: "", offset: 2047869976576, remaining: 0, isHidden: true, operationalStatus: "Other"),
            Partition(5, 1, 16759808, null, "", type: "MicrosoftReserved", fileSystem: "", offset: 17408, remaining: 0, isHidden: true, operationalStatus: "Other"),
            Partition(5, 2, 4096787480576, "E", "宏碁GM7", offset: 16777216, remaining: 455208955904),
            Partition(14, 1, 11049338142720, "I", "Pool02", allocationUnitSize: 65536, offset: 1048576, remaining: 3088199254016),
            Partition(15, 1, 9241693782016, "H", "Pool01", allocationUnitSize: 65536, offset: 1048576, remaining: 534690791424)
        };

        var networkDisks = new[]
        {
            Network("sim:network:r", "R", "网络磁盘", Gib(1759.9)),
            Network("sim:network:s", "S", "网络磁盘", Gib(4129.07)),
            Network("sim:network:t", "T", "新加卷", Gib(14901.4))
        };

        return new StorageSnapshot(
            2,
            SimulatedSnapshotVersion,
            DateTimeOffset.Now,
            new ComputerInfo(
                ComputerId,
                SimulatedComputerName,
                "Windows 10 Pro",
                "22H2",
                "19045",
                DateTimeOffset.Now.AddDays(-3),
                "22H2",
                "7184"),
            [new StorageSubsystemInfo("sim:subsystem:spaces", "Windows Storage Spaces", "Healthy", "OK")],
            disks,
            pools,
            tiers,
            virtualDisks,
            osDisks,
            partitions,
            networkDisks,
            [],
            []);
    }

    private static PhysicalDiskInfo Disk(
        string id,
        int number,
        string friendlyName,
        string model,
        string media,
        long size,
        string? poolId = null,
        bool isSystem = false,
        string bus = "SATA",
        long physicalSector = 4096,
        string interfaceType = "",
        string firmware = "",
        string provisioning = "") =>
        new(
            id, true, friendlyName, model, $"SIM••••{number:00}", bus, media, size,
            512, physicalSector, "Healthy", "OK", poolId?.EndsWith(":primordial", StringComparison.Ordinal) == true && !isSystem,
            isSystem ? "系统盘受保护" : poolId?.EndsWith(":primordial", StringComparison.Ordinal) == true ? string.Empty : "已属于存储池",
            number, isSystem, isSystem, isSystem, isSystem, poolId,
            firmware, interfaceType, provisioning);

    private static StorageTierInfo Tier(
        string id,
        string name,
        string media,
        string resiliency,
        long size,
        string poolId,
        string virtualDiskId,
        IReadOnlyList<string> members,
        int? numberOfColumns = null) =>
        new(id, true, name, media, resiliency, size, size, poolId, virtualDiskId, members,
            numberOfColumns, numberOfColumns is null ? null : 64 * 1024);

    private static OsDiskInfo OsDisk(
        int number,
        string friendlyName,
        long size,
        string? physicalId = null,
        string? virtualId = null,
        bool isSystem = false) =>
        new(
            $"sim:osdisk:{number}", friendlyName, number, "GPT", size,
            isSystem, isSystem, false, physicalId, virtualId);

    private static PartitionInfo Partition(
        int disk,
        int number,
        long size,
        string? driveLetter,
        string label,
        bool isSystem = false,
        bool isBoot = false,
        string type = "Primary",
        string fileSystem = "NTFS",
        long allocationUnitSize = 4096,
        long offset = 0,
        long remaining = 0,
        bool isHidden = false,
        string operationalStatus = "OK") =>
        new(
            $"sim:partition:{disk}:{number}", true, disk, number,
            type, offset, size,
            isBoot, isSystem, driveLetter ?? string.Empty, label, fileSystem,
            string.IsNullOrWhiteSpace(fileSystem) ? null : allocationUnitSize,
            remaining, "Healthy", operationalStatus,
            string.IsNullOrWhiteSpace(driveLetter) ? string.Empty : $"{driveLetter}:\\",
            $"sim:osdisk:{disk}",
            isHidden);

    private static NetworkDiskInfo Network(
        string id,
        string driveLetter,
        string label,
        long size) =>
        new(
            id, true, $"{driveLetter}: {label}", driveLetter,
            $"\\\\simulation\\{driveLetter.ToLowerInvariant()}", "NTFS", size, size / 3);

    private static long Gib(double value) => checked((long)(value * 1024 * 1024 * 1024));

}
