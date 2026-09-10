using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationSnapshotAuditorTests
{
    [Fact]
    public void DetectsOverlappingPartitionsAndDuplicateLetters()
    {
        var os = new OsDiskInfo("os:1", "Disk", 1, "GPT", 10_000, false, false, false, "p:1", null);
        var a = new PartitionInfo(
            "part:a", true, 1, 1, "Primary", 100, 500, false, false,
            "C", "", "NTFS", 4096, 100, "Healthy", "OK", "C:\\", "os:1");
        var b = new PartitionInfo(
            "part:b", true, 1, 2, "Primary", 200, 500, false, false,
            "C", "", "NTFS", 4096, 100, "Healthy", "OK", "C:\\", "os:1");
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "PC", "Windows", "10", "1", DateTimeOffset.UtcNow),
            [],
            [],
            [],
            [],
            [],
            [os],
            [a, b],
            [
                new VolumeInfo("vol:a", true, "part:a", "NTFS", "", 500, 100, 4096, "Healthy", "OK", ["C:\\"]),
                new VolumeInfo("vol:b", true, "part:b", "NTFS", "", 500, 100, 4096, "Healthy", "OK", ["C:\\"])
            ],
            [],
            [],
            []);
        var findings = SimulationSnapshotAuditor.Audit(snapshot, "sample");
        Assert.Contains(findings, item => item.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, item => item.Contains("Drive letter C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CuratedLayoutsHaveNoIntegrityFindings()
    {
        foreach (var layout in SimulationLayouts.CreateAll())
        {
            var findings = SimulationSnapshotAuditor.Audit(layout.Snapshot, layout.Name);
            Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
        }
    }
}
