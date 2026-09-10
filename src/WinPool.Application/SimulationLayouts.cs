using WinPool.Domain;

namespace WinPool.Application;

/// <summary>
/// Curated built-in simulation snapshots for the structure and partition
/// editors. Geometry, letters, and membership are internally consistent.
/// </summary>
public static class SimulationLayouts
{
    public static IReadOnlyList<(string Id, string Name, StorageSnapshot Snapshot)> CreateAll() =>
    [
        ("simulation:builtin:layout-primordial-ready", "建池与初始化", PrimordialReady()),
        ("simulation:builtin:layout-standard-tiered", "标准两层池", StandardTiered()),
        ("simulation:builtin:layout-pool-no-vdisk", "空池待建虚拟磁盘", PoolWithoutVirtualDisk()),
        ("simulation:builtin:layout-single-disk-pool", "单盘池", SingleDiskPool()),
        ("simulation:builtin:layout-spare-retired", "热备与退役", SpareAndRetired()),
        ("simulation:builtin:layout-dual-vd", "双虚拟磁盘", DualVirtualDisk()),
        ("simulation:builtin:layout-partition-edges", "分区边界", PartitionEdges()),
        ("simulation:builtin:layout-other-network", "其它与网络", OtherAndNetwork())
    ];

    public static StorageSnapshot PrimordialReady()
    {
        var b = new LayoutBuilder("pr");
        b.SystemPhysical(0, "Boot NVMe");
        b.RawPhysical(1, "RAW-SSD-1", "SSD");
        b.RawPhysical(2, "RAW-SSD-2", "SSD");
        b.RawPhysical(3, "RAW-HDD-1", "HDD");
        b.RawPhysical(4, "RAW-HDD-2", "HDD");
        b.RawPhysical(5, "RAW-HDD-3", "HDD");
        b.GptEmptyPhysical(6, "GPT-empty-SSD", "SSD");
        b.Primordial(0, 1, 2, 3, 4, 5, 6);
        return b.Build("建池与初始化", "layout-primordial-ready-v1");
    }

    public static StorageSnapshot StandardTiered()
    {
        var b = new LayoutBuilder("st");
        b.SystemPhysical(0, "Boot NVMe");
        b.Disk(1, "SSD-1", "SSD", "pool");
        b.Disk(2, "SSD-2", "SSD", "pool");
        b.Disk(3, "HDD-1", "HDD", "pool");
        b.Disk(4, "HDD-2", "HDD", "pool");
        b.Disk(5, "HDD-3", "HDD", "pool");
        b.Disk(6, "HDD-4", "HDD", "pool");
        b.Primordial(0);
        b.Pool("pool", "Pool01", 1, 2, 3, 4, 5, 6);
        b.Tier("perf", "Performance", "SSD", "pool", "vd", Mirror: true, 1, 2);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd", 3, 4, 5, 6);
        b.VirtualDisk("vd", "Pool01", "pool", "Mirror", 1, 2, 20, "perf", "cap");
        b.OsForVirtualNtfs("vd", "Pool01");
        return b.Build("标准两层池", "layout-standard-tiered-v1");
    }

    public static StorageSnapshot PoolWithoutVirtualDisk()
    {
        var b = new LayoutBuilder("nv");
        b.SystemPhysical(0, "Boot NVMe");
        b.Disk(1, "SSD-1", "SSD", "pool");
        b.Disk(2, "SSD-2", "SSD", "pool");
        b.Disk(3, "HDD-1", "HDD", "pool");
        b.Disk(4, "HDD-2", "HDD", "pool");
        b.Disk(5, "HDD-3", "HDD", "pool");
        b.Primordial(0);
        b.Pool("pool", "EmptyPool", 1, 2, 3, 4, 5);
        return b.Build("空池待建虚拟磁盘", "layout-pool-no-vdisk-v1");
    }

    public static StorageSnapshot SingleDiskPool()
    {
        var b = new LayoutBuilder("sd");
        b.SystemPhysical(0, "Boot NVMe");
        b.Disk(1, "Solo-SSD", "SSD", "pool");
        b.Primordial(0);
        b.Pool("pool", "SoloPool", 1);
        return b.Build("单盘池", "layout-single-disk-pool-v1");
    }

    public static StorageSnapshot SpareAndRetired()
    {
        var b = new LayoutBuilder("sr");
        b.SystemPhysical(0, "Boot NVMe");
        b.Disk(1, "SSD-1", "SSD", "pool");
        b.Disk(2, "HDD-1", "HDD", "pool");
        b.Disk(3, "HDD-2", "HDD", "pool");
        b.Disk(4, "HDD-3", "HDD", "pool");
        b.Disk(5, "Spare-HDD", "HDD", "pool", usage: PhysicalDiskUsage.HotSpare);
        b.Disk(6, "Retired-HDD", "HDD", "pool", usage: PhysicalDiskUsage.Retired);
        b.Primordial(0);
        b.Pool("pool", "PoolWithSpares", 1, 2, 3, 4, 5, 6);
        b.Tier("perf", "Performance", "SSD", "pool", "vd", 1);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd", 2, 3, 4);
        b.VirtualDisk("vd", "Data", "pool", "Simple", 1, 1, 20, "perf", "cap");
        b.OsForVirtualNtfs("vd", "Data");
        return b.Build("热备与退役", "layout-spare-retired-v1");
    }

    public static StorageSnapshot DualVirtualDisk()
    {
        var b = new LayoutBuilder("dv");
        b.SystemPhysical(0, "Boot NVMe");
        b.Disk(1, "SSD-A", "SSD", "pool");
        b.Disk(2, "SSD-B", "SSD", "pool");
        b.Disk(3, "HDD-A", "HDD", "pool");
        b.Disk(4, "HDD-B", "HDD", "pool");
        b.Disk(5, "HDD-C", "HDD", "pool");
        b.Primordial(0);
        b.Pool("pool", "DualVdPool", 1, 2, 3, 4, 5);
        b.Tier("perf", "Performance", "SSD", "pool", "vd1", Mirror: true, 1, 2);
        b.Tier("cap", "Capacity", "HDD", "pool", "vd1", 3, 4, 5);
        b.VirtualDisk("vd1", "DataVD", "pool", "Mirror", 1, 2, 21, "perf", "cap");
        b.VirtualDisk("vd2", "LogVD", "pool", "Simple", 1, 1, 22);
        b.OsForVirtualNtfs("vd1", "DataVD");
        b.OsForVirtualNtfs("vd2", "LogVD");
        return b.Build("双虚拟磁盘", "layout-dual-vd-v2");
    }

    public static StorageSnapshot PartitionEdges()
    {
        var b = new LayoutBuilder("pe");
        b.SystemPhysical(0, "Boot NVMe");
        b.RawPhysical(1, "Uninitialized-SSD", "SSD");
        b.GptEmptyPhysical(2, "GPT-MSR-only", "SSD");
        b.MixedFileSystemPhysical(3, "FS-mix-SSD", "SSD");
        b.OfflineNtfsPhysical(4, "Offline-HDD", "HDD");
        b.Primordial(0, 1, 2, 3, 4);
        return b.Build("分区边界", "layout-partition-edges-v1");
    }

    public static StorageSnapshot OtherAndNetwork()
    {
        var b = new LayoutBuilder("on");
        b.SystemPhysical(0, "Boot NVMe");
        b.Primordial(0);
        b.OtherNtfs(40, "USB-Other");
        b.Network("R", "share-r");
        b.Network("S", "share-s");
        return b.Build("其它与网络", "layout-other-network-v1");
    }

    private sealed class LayoutBuilder
    {
        private const long Megabyte = 1024L * 1024;
        private readonly string _prefix;
        private readonly string _subsystemId;
        private readonly HashSet<string> _letters = new(StringComparer.OrdinalIgnoreCase);
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

        public void Disk(
            int number,
            string name,
            string media,
            string poolKey,
            bool system = false,
            string usage = "",
            long? size = null)
        {
            var bytes = size ?? (media == "HDD" ? Gib(4000) : Gib(1000));
            _disks.Add(new PhysicalDiskInfo(
                Id($"disk:{number:00}"),
                true,
                name,
                name,
                $"SIM••••{number:00}",
                media == "HDD" ? "SAS" : "NVMe",
                media,
                bytes,
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
                Id($"pool:{poolKey}"),
                "",
                media == "HDD" ? "SAS" : "SCSI",
                "Fixed",
                "",
                usage));
        }

        public void SystemPhysical(int number, string name)
        {
            Disk(number, name, "SSD", "primordial", system: true, size: Gib(512));
            var disk = DiskByNumber(number);
            var osId = AddOs(number, disk.FriendlyName, disk.Size, disk.StableId, null, "GPT", system: true, offline: false);
            var offset = Megabyte;
            var part = 1;
            AddPartition(osId, number, ref part, ref offset, Megabyte * 100, "EfiSystem", "", "", true, false, true);
            AddPartition(osId, number, ref part, ref offset, Megabyte * 16, "MicrosoftReserved", "", "", false, false, true);
            var recovery = Megabyte * 500;
            var windows = disk.Size - offset - recovery;
            AddPartition(
                osId, number, ref part, ref offset, windows, "Primary", "NTFS", TakeLetter(),
                isSystem: false, isBoot: true, hidden: false, label: "", cluster: 65536);
            AddPartition(osId, number, ref part, ref offset, recovery, "WindowsRecovery", "", "Recovery", false, false, true);
        }

        public void RawPhysical(int number, string name, string media)
        {
            Disk(number, name, media, "primordial");
            var disk = DiskByNumber(number);
            AddOs(number, disk.FriendlyName, disk.Size, disk.StableId, null, "RAW", system: false, offline: false);
        }

        public void GptEmptyPhysical(int number, string name, string media)
        {
            Disk(number, name, media, "primordial");
            var disk = DiskByNumber(number);
            var osId = AddOs(number, disk.FriendlyName, disk.Size, disk.StableId, null, "GPT", false, false);
            var offset = Megabyte;
            var part = 1;
            AddPartition(osId, number, ref part, ref offset, Megabyte * 16, "MicrosoftReserved", "", "", false, false, true);
        }

        public void MixedFileSystemPhysical(int number, string name, string media)
        {
            Disk(number, name, media, "primordial", size: Gib(2000));
            var disk = DiskByNumber(number);
            var osId = AddOs(number, disk.FriendlyName, disk.Size, disk.StableId, null, "GPT", false, false);
            var offset = Megabyte;
            var part = 1;
            AddPartition(osId, number, ref part, ref offset, Megabyte * 16, "MicrosoftReserved", "", "", false, false, true);
            AddPartition(osId, number, ref part, ref offset, Gib(200), "Primary", "NTFS", TakeLetter(), cluster: 65536, label: "NTFS-Vol");
            AddPartition(osId, number, ref part, ref offset, Gib(200), "Primary", "ReFS", TakeLetter(), cluster: 65536, label: "ReFS-Vol");
            AddPartition(osId, number, ref part, ref offset, Gib(100), "Primary", "exFAT", TakeLetter(), cluster: 65536, label: "exFAT-Vol");
            AddPartition(osId, number, ref part, ref offset, Gib(80), "Primary", "", "", false, false, false, label: "");
        }

        public void OfflineNtfsPhysical(int number, string name, string media)
        {
            Disk(number, name, media, "primordial");
            var disk = DiskByNumber(number);
            var osId = AddOs(number, disk.FriendlyName, disk.Size, disk.StableId, null, "GPT", false, offline: true);
            var offset = Megabyte;
            var part = 1;
            AddPartition(osId, number, ref part, ref offset, Megabyte * 16, "MicrosoftReserved", "", "", false, false, true);
            AddPartition(
                osId, number, ref part, ref offset, disk.Size - offset, "Primary", "NTFS", TakeLetter(),
                cluster: 65536, label: "Offline");
        }

        public void Primordial(params int[] diskNumbers) =>
            Pool("primordial", "Primordial", diskNumbers, primordial: true);

        public void Pool(string key, string name, params int[] diskNumbers) =>
            Pool(key, name, diskNumbers, primordial: false);

        public void Tier(
            string key,
            string name,
            string media,
            string poolKey,
            string virtualDiskKey,
            params int[] diskNumbers) =>
            Tier(key, name, media, poolKey, virtualDiskKey, Mirror: false, diskNumbers);

        public void Tier(
            string key,
            string name,
            string media,
            string poolKey,
            string virtualDiskKey,
            bool Mirror,
            params int[] diskNumbers)
        {
            var members = diskNumbers.Select(n => Id($"disk:{n:00}")).ToArray();
            var sizes = members.Select(id => _disks.First(d => d.StableId == id).Size).ToArray();
            var resiliency = media == "HDD" ? "Parity" : Mirror && diskNumbers.Length >= 2 ? "Mirror" : "Simple";
            var copies = resiliency == "Mirror" ? 2 : 1;
            var columns = resiliency == "Parity" ? Math.Max(1, diskNumbers.Length - 1) : 1;
            var redundancy = resiliency == "Parity" || resiliency == "Mirror" ? 1 : 0;
            var logical = resiliency == "Parity"
                ? sizes.Sum() * Math.Max(1, diskNumbers.Length - 1) / diskNumbers.Length
                : ConservativeCapacity.PlanLogicalUpperBound(sizes, copies, 65536).AlignedLogicalBytes;
            _tiers.Add(new StorageTierInfo(
                Id($"tier:{key}"),
                true,
                name,
                media,
                resiliency,
                logical,
                sizes.Sum(),
                Id($"pool:{poolKey}"),
                Id($"vdisk:{virtualDiskKey}"),
                members,
                columns,
                65536,
                copies,
                redundancy,
                CapacitySourceKind.SimulatedEstimate));
        }

        public void VirtualDisk(
            string key,
            string name,
            string poolKey,
            string resiliency,
            int columns,
            int copies,
            int osNumber,
            params string[] tierKeys)
        {
            var tierIds = tierKeys.Select(t => Id($"tier:{t}")).ToArray();
            var logical = tierIds.Length == 0
                ? Gib(200)
                : _tiers.Where(tier => tierIds.Contains(tier.StableId)).Sum(tier => tier.Size);
            if (logical <= 0)
            {
                logical = Gib(200);
            }

            _virtualDisks.Add(new VirtualDiskInfo(
                Id($"vdisk:{key}"),
                true,
                name,
                "Healthy",
                "OK",
                resiliency,
                "Fixed",
                columns,
                65536,
                logical,
                logical,
                Id($"pool:{poolKey}"),
                tierIds,
                [osNumber],
                CapacitySourceKind.SimulatedEstimate));
        }

        public void OsForVirtualNtfs(string virtualDiskKey, string name)
        {
            var vdisk = _virtualDisks.First(item => item.StableId == Id($"vdisk:{virtualDiskKey}"));
            var osNumber = vdisk.OsDiskNumbers[0];
            var osId = AddOs(osNumber, name, vdisk.Size, null, vdisk.StableId, "GPT", false, false);
            var offset = Megabyte;
            var part = 1;
            AddPartition(osId, osNumber, ref part, ref offset, Megabyte * 16, "MicrosoftReserved", "", "", false, false, true);
            AddPartition(
                osId, osNumber, ref part, ref offset, vdisk.Size - offset, "Primary", "NTFS", TakeLetter(),
                cluster: 65536, label: name);
        }

        public void OtherNtfs(int osNumber, string name)
        {
            var size = Gib(128);
            var osId = AddOs(osNumber, name, size, null, null, "GPT", false, false);
            var offset = Megabyte;
            var part = 1;
            AddPartition(
                osId, osNumber, ref part, ref offset, size - offset, "Primary", "NTFS", TakeLetter(),
                cluster: 65536, label: name);
        }

        public void Network(string letter, string label)
        {
            TakeLetter(letter);
            _networks.Add(new NetworkDiskInfo(
                Id($"network:{letter.ToLowerInvariant()}"),
                true,
                $"{letter}: {label}",
                letter,
                $"\\\\simulation\\{letter.ToLowerInvariant()}",
                "NTFS",
                Gib(1024),
                Gib(400)));
        }

        public StorageSnapshot Build(string computerName, string version)
        {
            var allocated = _virtualDisks.Sum(item => item.Size);
            var pools = _pools
                .Select(pool => pool.IsPrimordial
                    ? pool
                    : pool with { AllocatedSize = allocated > 0 && _virtualDisks.Any(item => item.PoolStableId == pool.StableId)
                        ? _virtualDisks.Where(item => item.PoolStableId == pool.StableId).Sum(item => item.Size)
                        : 0 })
                .ToArray();
            var snapshot = new StorageSnapshot(
                StorageSnapshot.CurrentSchemaVersion,
                version,
                DateTimeOffset.UnixEpoch.AddDays(1),
                new ComputerInfo(
                    $"simulation:system:{_prefix}",
                    computerName,
                    "Windows 10 Pro",
                    "22H2",
                    "19045",
                    DateTimeOffset.UnixEpoch,
                    "22H2",
                    "7184"),
                [new StorageSubsystemInfo(_subsystemId, "Windows Storage Spaces", "Healthy", "OK")],
                _disks,
                pools,
                _tiers,
                _virtualDisks,
                _osDisks,
                _partitions,
                VolumesFrom(_partitions),
                _networks,
                [],
                []);
            snapshot = EditWorkspace.EnsureFreeDisksHaveOsDisks(snapshot);
            return StorageRelationshipProjector.Rebuild(snapshot);
        }

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
                0,
                _subsystemId,
                members));
        }

        private string AddOs(
            int number,
            string name,
            long size,
            string? physicalId,
            string? virtualId,
            string style,
            bool system,
            bool offline)
        {
            var osId = Id($"osdisk:{number}");
            _osDisks.Add(new OsDiskInfo(
                osId, name, number, style, size, system, system, offline, physicalId, virtualId));
            return osId;
        }

        private void AddPartition(
            string osId,
            int diskNumber,
            ref int partNumber,
            ref long offset,
            long size,
            string type,
            string fileSystem,
            string letter,
            bool isSystem = false,
            bool isBoot = false,
            bool hidden = false,
            string label = "",
            long? cluster = null)
        {
            if (size <= 0)
            {
                throw new InvalidOperationException($"Partition size must be positive on disk {diskNumber}.");
            }

            var formatted = !string.IsNullOrWhiteSpace(fileSystem);
            var path = letter.Length == 1 ? $"{letter}:\\" : string.Empty;
            _partitions.Add(new PartitionInfo(
                Id($"partition:{diskNumber}:{partNumber}"),
                true,
                diskNumber,
                partNumber,
                type,
                offset,
                size,
                isBoot,
                isSystem,
                letter,
                label,
                fileSystem,
                formatted ? cluster ?? 65536 : null,
                formatted ? size * 4 / 5 : 0,
                "Healthy",
                "OK",
                path,
                osId,
                hidden));
            partNumber++;
            offset += size;
        }

        private static IReadOnlyList<VolumeInfo> VolumesFrom(IEnumerable<PartitionInfo> partitions) =>
            partitions
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.FileSystem)
                    || !string.IsNullOrWhiteSpace(item.DriveLetter))
                .Select(item => new VolumeInfo(
                    $"sim:volume:{item.StableId}",
                    item.IsStable,
                    item.StableId,
                    item.FileSystem,
                    item.FileSystemLabel,
                    item.Size,
                    item.SizeRemaining,
                    item.AllocationUnitSize,
                    item.HealthStatus,
                    item.OperationalStatus,
                    string.IsNullOrWhiteSpace(item.Path) ? [] : [item.Path]))
                .ToArray();

        private PhysicalDiskInfo DiskByNumber(int number) =>
            _disks.First(item => item.DeviceId == number);

        private string TakeLetter(string? required = null)
        {
            if (!string.IsNullOrWhiteSpace(required))
            {
                var token = required.Trim().ToUpperInvariant();
                if (!_letters.Add(token))
                {
                    throw new InvalidOperationException($"Drive letter {token}: is already used.");
                }

                return token;
            }

            foreach (var candidate in "CDEFGHIJKLMNOPQRSTUVWXYZ")
            {
                var token = candidate.ToString();
                if (_letters.Add(token))
                {
                    return token;
                }
            }

            throw new InvalidOperationException("No free drive letters remain.");
        }

        private string Id(string suffix) => $"sim:{_prefix}:{suffix}";

        private static long Gib(double value) => checked((long)(value * 1024 * 1024 * 1024));
    }
}
