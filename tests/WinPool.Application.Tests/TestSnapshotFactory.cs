using WinPool.Application;

namespace WinPool.Application.Tests;

internal static class TestSnapshotFactory
{
    public static StorageSnapshot Create()
    {
        var physical = new PhysicalDiskInfo(
            "physical:1", true, "Disk One", "Model", "AB••••YZ", "SATA", "HDD",
            2_000_000, 512, 4096, "Healthy", "OK", false, "In a pool", 1,
            false, false, false, false, "pool:1");
        var pool = new StoragePoolInfo(
            "pool:1", true, "Pool01", false, "Healthy", "OK", 2_000_000, 1_000_000, "subsystem:1", ["physical:1"]);
        var tier = new StorageTierInfo(
            "tier:1", true, "Capacity", "HDD", "Parity", 1_000_000, 1_000_000,
            "pool:1", "virtual:1", ["physical:1"]);
        var virtualDisk = new VirtualDiskInfo(
            "virtual:1", true, "Virtual01", "Healthy", "OK", "Parity", "Fixed",
            3, 65536, 1_000_000, 1_000_000, "pool:1", ["tier:1"], [3]);
        var osDisk = new OsDiskInfo(
            "osdisk:3", "Virtual01", 3, "GPT", 1_000_000, false, false, false, null, "virtual:1");
        var partition = new PartitionInfo(
            "partition:1", true, 3, 1, "Primary", 1048576, 900_000, false, false,
            "C", "Data", "NTFS", 4096, 400_000, "Healthy", "OK", "C:\\", "osdisk:3");
        return new StorageSnapshot(
            2, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [physical], [pool], [tier], [virtualDisk], [osDisk], [partition], [],
            [new StorageRelationship("pool:1", "physical:1", "PoolMember")],
            []);
    }
}
