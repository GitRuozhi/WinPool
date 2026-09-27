using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class StorageSystemImportValidatorTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void ImportRejectsOverlappingPartitionsBeforeTheyCanBeSaved()
    {
        var document = Document(
            Partition("part:a", MiB, 4 * MiB),
            Partition("part:b", 3 * MiB, 2 * MiB));

        var error = Assert.Throws<InvalidDataException>(
            () => StorageSystemImportValidator.Validate(document));
        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportRejectsPartitionBeyondKnownDiskAndOverflowingEnd()
    {
        var beyond = Document(Partition("part:beyond", 9 * MiB, 2 * MiB));
        Assert.Contains("beyond disk", Assert.Throws<InvalidDataException>(
            () => StorageSystemImportValidator.Validate(beyond)).Message);

        var overflow = Document(Partition("part:overflow", long.MaxValue - 1, 4));
        Assert.Contains("overflowing end", Assert.Throws<InvalidDataException>(
            () => StorageSystemImportValidator.Validate(overflow)).Message);
    }

    [Fact]
    public void ImportPreservesValidAndUnlinkedPartitionsWithoutInferringDiskIdentity()
    {
        StorageSystemImportValidator.Validate(Document(
            Partition("part:a", MiB, 2 * MiB),
            Partition("part:b", 3 * MiB, 2 * MiB)));

        // The same DiskNumber is not proof that two unlinked source objects
        // belong to one disk.
        StorageSystemImportValidator.Validate(Document(
            Partition("part:unlinked-a", MiB, 2 * MiB) with { OsDiskStableId = null },
            Partition("part:unlinked-b", MiB, 2 * MiB) with { OsDiskStableId = null }));
    }

    [Fact]
    public void GeometryAuditDoesNotTreatMissingSourceSizeAsMeasuredZero()
    {
        var snapshot = StorageSnapshot.Empty("TEST-PC") with
        {
            Partitions = [Partition("part:unknown", MiB, 0)],
            FieldIssues = [new StorageFieldIssue(
                "part:unknown", "Size", FieldReadState.NotCollected, "Unavailable")]
        };

        Assert.Empty(SimulationSnapshotAuditor.AuditPartitionGeometry(snapshot));
    }

    [Fact]
    public void GeometryAuditRejectsKnownInvalidFieldEvenWhenOtherDimensionIsUnknown()
    {
        var negativeOffset = StorageSnapshot.Empty("TEST-PC") with
        {
            Partitions = [Partition("part:negative", -1, 0)],
            FieldIssues = [new StorageFieldIssue(
                "part:negative", "Size", FieldReadState.NotCollected, "Unavailable")]
        };
        Assert.Contains(SimulationSnapshotAuditor.AuditPartitionGeometry(negativeOffset),
            error => error.Contains("invalid offset", StringComparison.Ordinal));

        var zeroSize = StorageSnapshot.Empty("TEST-PC") with
        {
            Partitions = [Partition("part:zero", 0, 0)],
            FieldIssues = [new StorageFieldIssue(
                "part:zero", "Offset", FieldReadState.NotCollected, "Unavailable")]
        };
        Assert.Contains(SimulationSnapshotAuditor.AuditPartitionGeometry(zeroSize),
            error => error.Contains("invalid offset", StringComparison.Ordinal));
    }

    private static StorageSystemDocument Document(params PartitionInfo[] partitions)
    {
        var snapshot = StorageSnapshot.Empty("TEST-PC") with
        {
            OsDisks = [new OsDiskInfo("os:1", "Disk", 1, "GPT", 10 * MiB,
                false, false, false, null, null)],
            Partitions = partitions
        };
        return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
            "simulation:import", StorageSystemKind.Simulation, "Imported", snapshot,
            [], DateTimeOffset.UtcNow);
    }

    private static PartitionInfo Partition(string id, long offset, long size) =>
        new(id, true, 1, 1, "BasicData", offset, size, false, false,
            "", "", "", null, 0, "Healthy", "OK", "", "os:1");
}
