using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

public static class SimulationCatalog
{
    public const string ReferenceDocumentId =
        "simulation:builtin:desktop-pl96ukd-20260727-114130";

    public static IReadOnlyList<StorageSystemDocument> CreateDocuments()
    {
        return
        [
            Document(
                ReferenceDocumentId,
                "DESKTOP-PL96UKD",
                SimulationStorageSnapshotFactory.Create(),
                KsReferenceReportFactory.Create()),
            Document(
                "simulation:builtin:layout-triple-tier",
                "三层池",
                TripleTier()),
            Document(
                "simulation:builtin:layout-dual-vd",
                "双虚拟磁盘",
                DualVirtualDisk()),
            Document(
                "simulation:builtin:layout-tall-system",
                "超高系统盘",
                TallSystemDisk()),
            Document(
                "simulation:builtin:layout-wide-16",
                "16盘无层",
                WideSixteenDisks()),
            Document(
                "simulation:builtin:layout-eight-pools",
                "8个小池",
                EightSmallPools()),
            Document(
                "simulation:builtin:layout-network-12",
                "12网络盘",
                TwelveNetworkDisks()),
            Document(
                "simulation:builtin:layout-perf4-cap4",
                "性能4容量4",
                PerfFourCapFour()),
            Document(
                "simulation:builtin:layout-other-group",
                "其它组",
                OtherGroup()),
            Document(
                "simulation:builtin:layout-direct-spares",
                "直连热备",
                DirectSpares())
        ];
    }

    public static StorageSystemDocument? TryCreateDocument(string id) =>
        CreateDocuments().FirstOrDefault(
            document => document.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static StorageSystemDocument Document(
        string id,
        string name,
        StorageSnapshot snapshot,
        HardwareInventoryReport? hardwareReport = null) =>
        new(
            StorageSystemDocument.CurrentSchemaVersion,
            id,
            StorageSystemKind.Simulation,
            name,
            snapshot,
            hardwareReport ?? HardwareInventoryReport.Empty(snapshot.ScannedAt),
            [],
            snapshot.ScannedAt);

    private static StorageSnapshot TripleTier()
    {
        var b = new LayoutBuilder("tt");
        b.PartitionedPhysical(0, "System SSD", "SSD", "primordial", 3, system: true);
        b.Disk(1, "Cache-1", "SSD", "pool");
        b.Disk(2, "Cache-2", "SSD", "pool");
        b.Disk(3, "Perf-1", "SSD", "pool");
        b.Disk(4, "Perf-2", "SSD", "pool");
        b.Disk(5, "Perf-3", "SSD", "pool");
        b.Disk(6, "Perf-4", "SSD", "pool");
        for (var i = 7; i <= 14; i++)
        {
            b.Disk(i, $"Cap-{i - 6}", "HDD", "pool");
        }

        b.Primordial(0);
        b.Pool("pool", "TripleTier", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14);
        b.Tier("cache", "Cache", "SCM", "pool", "vd", 1, 2);
        b.Tier("perf", "Performance", "SSD", "pool", "vd", 3, 4, 5, 6);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd", 7, 8, 9, 10, 11, 12, 13, 14);
        b.VirtualDisk("vd", "TripleVD", "pool", 20, "cache", "perf", "cap");
        b.OsForVirtual(20, "vd", "TripleVD", 1);
        return b.Build("三层池", "layout-triple-tier-v1");
    }

    private static StorageSnapshot DualVirtualDisk()
    {
        var b = new LayoutBuilder("dv");
        b.PartitionedPhysical(0, "Boot", "SSD", "primordial", 4, system: true);
        b.Disk(1, "SSD-A", "SSD", "pool");
        b.Disk(2, "SSD-B", "SSD", "pool");
        b.Disk(3, "HDD-A", "HDD", "pool");
        b.Disk(4, "HDD-B", "HDD", "pool");
        b.Disk(5, "HDD-C", "HDD", "pool");
        b.Primordial(0);
        b.Pool("pool", "DualVdPool", 1, 2, 3, 4, 5);
        b.Tier("perf", "Performance", "SSD", "pool", "vd1", 1, 2);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd1", 3, 4, 5);
        b.VirtualDisk("vd1", "DataVD", "pool", 21, "perf", "cap");
        b.VirtualDisk("vd2", "LogVD", "pool", 22);
        b.OsForVirtual(21, "vd1", "DataVD", 1);
        b.OsForVirtual(22, "vd2", "LogVD", 5);
        return b.Build("双虚拟磁盘", "layout-dual-vd-v1");
    }

    private static StorageSnapshot TallSystemDisk()
    {
        var b = new LayoutBuilder("ts");
        b.PartitionedPhysical(0, "8-partition NVMe", "SSD", "primordial", 8, system: true);
        for (var i = 1; i <= 6; i++)
        {
            b.Disk(i, $"Empty-{i}", "HDD", "primordial");
        }

        b.Primordial(0, 1, 2, 3, 4, 5, 6);
        b.Network("R", "share-r");
        b.Network("S", "share-s");
        return b.Build("超高系统盘", "layout-tall-system-v1");
    }

    private static StorageSnapshot WideSixteenDisks()
    {
        var b = new LayoutBuilder("w16");
        b.PartitionedPhysical(0, "Boot", "SSD", "primordial", 2, system: true);
        var members = new int[16];
        for (var i = 1; i <= 16; i++)
        {
            b.Disk(i, $"JBOD-{i:00}", i <= 4 ? "SSD" : "HDD", "pool");
            members[i - 1] = i;
        }

        b.Primordial(0);
        b.Pool("pool", "FlatPool", members);
        return b.Build("16盘无层", "layout-wide-16-v1");
    }

    private static StorageSnapshot EightSmallPools()
    {
        var b = new LayoutBuilder("ep");
        b.PartitionedPhysical(0, "Boot", "SSD", "primordial", 2, system: true);
        b.Primordial(0);
        for (var i = 1; i <= 8; i++)
        {
            b.Disk(i, $"Solo-{i}", "HDD", $"p{i}");
            b.Pool($"p{i}", $"Pool{i:00}", i);
        }

        return b.Build("8个小池", "layout-eight-pools-v1");
    }

    private static StorageSnapshot TwelveNetworkDisks()
    {
        var b = new LayoutBuilder("n12");
        b.PartitionedPhysical(0, "Boot", "SSD", "primordial", 5, system: true);
        b.Primordial(0);
        for (var i = 0; i < 12; i++)
        {
            b.Network(((char)('F' + i)).ToString(), $"net-{i + 1}");
        }

        return b.Build("12网络盘", "layout-network-12-v1");
    }

    private static StorageSnapshot PerfFourCapFour()
    {
        var b = new LayoutBuilder("p4c4");
        b.PartitionedPhysical(0, "TallBoot", "SSD", "primordial", 6, system: true);
        b.Disk(1, "SSD-1", "SSD", "pool");
        b.Disk(2, "SSD-2", "SSD", "pool");
        b.Disk(3, "SSD-3", "SSD", "pool");
        b.Disk(4, "SSD-4", "SSD", "pool");
        b.Disk(5, "HDD-1", "HDD", "pool");
        b.Disk(6, "HDD-2", "HDD", "pool");
        b.Disk(7, "HDD-3", "HDD", "pool");
        b.Disk(8, "HDD-4", "HDD", "pool");
        b.Primordial(0);
        b.Pool("pool", "Pool01", 1, 2, 3, 4, 5, 6, 7, 8);
        b.Tier("perf", "Performance", "SSD", "pool", "vd", 1, 2, 3, 4);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd", 5, 6, 7, 8);
        b.VirtualDisk("vd", "Pool01", "pool", 30, "perf", "cap");
        b.OsForVirtual(30, "vd", "Pool01", 1);
        return b.Build("性能4容量4", "layout-perf4-cap4-v1");
    }

    private static StorageSnapshot OtherGroup()
    {
        var b = new LayoutBuilder("og");
        b.PartitionedPhysical(0, "Boot", "SSD", "primordial", 3, system: true);
        b.Primordial(0);
        b.OtherOs(40, "USB-Other", 3);
        b.OtherOs(41, "iSCSI-Other", 2);
        b.OtherOs(42, "VHD-Other", 4);
        return b.Build("其它组", "layout-other-group-v1");
    }

    private static StorageSnapshot DirectSpares()
    {
        var b = new LayoutBuilder("ds");
        b.PartitionedPhysical(0, "Boot", "SSD", "primordial", 2, system: true);
        b.Disk(1, "SSD-1", "SSD", "pool");
        b.Disk(2, "HDD-1", "HDD", "pool");
        b.Disk(3, "HDD-2", "HDD", "pool");
        b.Disk(4, "HDD-3", "HDD", "pool");
        b.Disk(5, "Spare-1", "HDD", "pool");
        b.Disk(6, "Spare-2", "HDD", "pool");
        b.Primordial(0);
        b.Pool("pool", "PoolWithSpares", 1, 2, 3, 4, 5, 6);
        b.Tier("perf", "Performance", "SSD", "pool", "vd", 1);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd", 2, 3, 4);
        b.VirtualDisk("vd", "Data", "pool", 50, "perf", "cap");
        b.OsForVirtual(50, "vd", "Data", 1);
        return b.Build("直连热备", "layout-direct-spares-v1");
    }

    private sealed class LayoutBuilder
    {
        private readonly string _prefix;
        private readonly string _subsystemId;
        private readonly List<PhysicalDiskInfo> _disks = [];
        private readonly List<StoragePoolInfo> _pools = [];
        private readonly List<StorageTierInfo> _tiers = [];
        private readonly List<VirtualDiskInfo> _virtualDisks = [];
        private readonly List<OsDiskInfo> _osDisks = [];
        private readonly List<PartitionInfo> _partitions = [];
        private readonly List<NetworkDiskInfo> _networks = [];

        public LayoutBuilder(string prefix)
        {
            _prefix = prefix;
            _subsystemId = Id("subsystem");
        }

        public void Disk(int number, string name, string media, string poolKey, bool system = false)
        {
            var size = media == "HDD" ? Gib(4000) : Gib(1000);
            _disks.Add(new PhysicalDiskInfo(
                Id($"disk:{number:00}"),
                true,
                name,
                name,
                $"SIM••••{number:00}",
                media == "HDD" ? "SAS" : "NVMe",
                media,
                size,
                512,
                4096,
                "Healthy",
                "OK",
                poolKey == "primordial" && !system,
                system ? "系统盘受保护" : poolKey == "primordial" ? string.Empty : "已属于存储池",
                number,
                system,
                system,
                system,
                system,
                Id($"pool:{poolKey}")));
        }

        public void PartitionedPhysical(
            int number,
            string name,
            string media,
            string poolKey,
            int partitions,
            bool system = false)
        {
            Disk(number, name, media, poolKey, system);
            OsForPhysical(number, partitions, system);
        }

        public void Primordial(params int[] diskNumbers) =>
            Pool("primordial", "Primordial", diskNumbers, primordial: true);

        public void Pool(string key, string name, params int[] diskNumbers) =>
            Pool(key, name, diskNumbers, primordial: false);

        private void Pool(string key, string name, int[] diskNumbers, bool primordial)
        {
            var members = diskNumbers.Select(n => Id($"disk:{n:00}")).ToArray();
            var size = members.Sum(id => _disks.First(d => d.StableId == id).Size);
            _pools.Add(new StoragePoolInfo(
                Id($"pool:{key}"),
                true,
                name,
                primordial,
                "Healthy",
                "OK",
                size,
                size / 2,
                _subsystemId,
                members));
        }

        public void Tier(
            string key,
            string name,
            string media,
            string poolKey,
            string virtualDiskKey,
            params int[] diskNumbers)
        {
            var members = diskNumbers.Select(n => Id($"disk:{n:00}")).ToArray();
            var size = members.Sum(id => _disks.First(d => d.StableId == id).Size);
            _tiers.Add(new StorageTierInfo(
                Id($"tier:{key}"),
                true,
                name,
                media,
                media == "HDD" ? "Parity" : "Simple",
                size / 2,
                size / 2,
                Id($"pool:{poolKey}"),
                Id($"vdisk:{virtualDiskKey}"),
                members,
                diskNumbers.Length,
                64 * 1024));
        }

        public void VirtualDisk(
            string key,
            string name,
            string poolKey,
            int osNumber,
            params string[] tierKeys)
        {
            var size = Gib(2000);
            _virtualDisks.Add(new VirtualDiskInfo(
                Id($"vdisk:{key}"),
                true,
                name,
                "Healthy",
                "OK",
                tierKeys.Length > 0 ? "Tiered" : "Simple",
                "Fixed",
                2,
                64 * 1024,
                size,
                size,
                Id($"pool:{poolKey}"),
                tierKeys.Select(t => Id($"tier:{t}")).ToArray(),
                [osNumber]));
        }

        public void OsForPhysical(int diskNumber, int partitions, bool system)
        {
            var disk = _disks.First(d => d.DeviceId == diskNumber);
            AddOsDisk(diskNumber, disk.FriendlyName, disk.Size, disk.StableId, null, system, partitions);
        }

        public void OsForVirtual(int osNumber, string virtualDiskKey, string name, int partitions)
        {
            var vdisk = _virtualDisks.First(d => d.StableId == Id($"vdisk:{virtualDiskKey}"));
            AddOsDisk(osNumber, name, vdisk.Size, null, vdisk.StableId, false, partitions);
        }

        public void OtherOs(int osNumber, string name, int partitions) =>
            AddOsDisk(osNumber, name, Gib(500), null, null, false, partitions);

        public void Network(string letter, string label) =>
            _networks.Add(new NetworkDiskInfo(
                Id($"network:{letter.ToLowerInvariant()}"),
                true,
                $"{letter}: {label}",
                letter,
                $"\\\\simulation\\{letter.ToLowerInvariant()}",
                "NTFS",
                Gib(1024),
                Gib(400)));

        public StorageSnapshot Build(string computerName, string version)
        {
            var computerId = $"simulation:system:{_prefix}";
            return new StorageSnapshot(
                2,
                version,
                DateTimeOffset.Now,
                new ComputerInfo(
                    computerId,
                    computerName,
                    "Windows 10 Pro",
                    "22H2",
                    "19045",
                    DateTimeOffset.Now.AddDays(-3),
                    "22H2",
                    "7184"),
                [new StorageSubsystemInfo(_subsystemId, "Windows Storage Spaces", "Healthy", "OK")],
                _disks,
                _pools,
                _tiers,
                _virtualDisks,
                _osDisks,
                _partitions,
                _networks,
                [],
                []);
        }

        private void AddOsDisk(
            int number,
            string name,
            long size,
            string? physicalId,
            string? virtualId,
            bool system,
            int partitions)
        {
            var osId = $"sim:{_prefix}:osdisk:{number}";
            _osDisks.Add(new OsDiskInfo(
                osId,
                name,
                number,
                "GPT",
                size,
                system,
                system,
                false,
                physicalId,
                virtualId));

            var remaining = size;
            for (var i = 1; i <= partitions; i++)
            {
                var (type, fileSystem, letter, label, isSystem, isBoot, isHidden, partSize) =
                    PartitionSpec(i, partitions, system, remaining);
                remaining = Math.Max(0, remaining - partSize);
                _partitions.Add(new PartitionInfo(
                    $"sim:{_prefix}:partition:{number}:{i}",
                    true,
                    number,
                    i,
                    type,
                    1024L * 1024 * i,
                    partSize,
                    isBoot,
                    isSystem,
                    letter,
                    label,
                    fileSystem,
                    string.IsNullOrWhiteSpace(fileSystem) ? null : 4096L,
                    partSize / 5,
                    "Healthy",
                    "OK",
                    string.IsNullOrWhiteSpace(letter) ? string.Empty : $"{letter}:\\",
                    osId,
                    isHidden));
            }
        }

        private static (
            string Type,
            string FileSystem,
            string Letter,
            string Label,
            bool IsSystem,
            bool IsBoot,
            bool IsHidden,
            long Size) PartitionSpec(int index, int count, bool systemDisk, long remaining)
        {
            if (systemDisk && index == 1)
            {
                return ("EfiSystem", "", "", "", true, false, true, 100L * 1024 * 1024);
            }

            if (systemDisk && index == 2 && count > 2)
            {
                return ("MicrosoftReserved", "", "", "", false, false, true, 16L * 1024 * 1024);
            }

            if (index == count && count >= 4)
            {
                return ("WindowsRecovery", "", "", "Recovery", false, false, true, 500L * 1024 * 1024);
            }

            var letter = ((char)('C' + Math.Max(0, index - (systemDisk ? 3 : 1)))).ToString();
            var size = Math.Max(Gib(8), remaining / Math.Max(1, count - index + 1));
            return ("Primary", "NTFS", letter, index == (systemDisk ? 3 : 1) ? "" : $"Vol{index}",
                false, systemDisk && index == 3, false, size);
        }

        private string Id(string suffix) => $"sim:{_prefix}:{suffix}";

        private static long Gib(double value) => checked((long)(value * 1024 * 1024 * 1024));
    }
}
