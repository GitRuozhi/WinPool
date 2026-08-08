using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Testing.Tests;

public sealed class CopyBatchPlannerTests
{
    [Fact]
    public void CompilesDeterministicHashBoundBatches()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var source = SourceEvidence(fixture, now);

        var manifest = new CopyBatchPlanner().Compile(
            fixture.Plan,
            fixture.CopyStep.Id,
            source,
            EmptyDestination(fixture),
            batchThresholdBytes: 100,
            maximumFilesPerBatch: 2,
            createdAtUtc: now);

        Assert.True(CopyBatchPlanner.HasValidHash(manifest));
        Assert.Equal([100L, 30L], manifest.Batches.Select(item => item.PlannedBytes));
        Assert.Equal([2, 1], manifest.Batches.Select(item => item.PlannedFileCount));
        Assert.Equal([1, 1, 2], manifest.Entries.Select(item => item.BatchNumber));
        Assert.Equal(
            ["a.dat", "b.dat", "nested\\c.dat"],
            manifest.Entries.Select(item => item.RelativePath));

        var duplicate = new CopyBatchPlanner().Compile(
            fixture.Plan,
            fixture.CopyStep.Id,
            source,
            EmptyDestination(fixture),
            100,
            2,
            now);
        Assert.Equal(manifest.ManifestHash, duplicate.ManifestHash);
        Assert.False(
            CopyBatchPlanner.HasValidHash(
                manifest with { BatchThresholdBytes = 101 }));
    }

    [Fact]
    public void RecoveryAcceptsOnlyExactTargetsAndReportsUnknownEntries()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var source = SourceEvidence(fixture, now);
        var planner = new CopyBatchPlanner();
        var manifest = planner.Compile(
            fixture.Plan,
            fixture.CopyStep.Id,
            source,
            EmptyDestination(fixture),
            100,
            2,
            now);
        var destination = EmptyDestination(fixture) with
        {
            ActualBytes = 150,
            ActualFileCount = 3,
            Entries =
            [
                source.Entries.Single(item => item.RelativePath == "a.dat"),
                source.Entries.Single(item => item.RelativePath == "b.dat")
                    with { Length = 41 },
                new(
                    "unknown.dat",
                    49,
                    FileAttributes.Normal,
                    now,
                    null)
            ]
        };

        var report = planner.Recover(manifest, source, destination);

        Assert.Equal(1, report.AcceptedCompletedCount);
        Assert.Equal(1, report.PendingCount);
        Assert.Equal(2, report.ConflictCount);
        Assert.Contains(
            report.Items,
            item => item.RelativePath == "a.dat"
                && item.Decision
                    is CopyBatchRecoveryDecision.AcceptCompletedTarget);
        Assert.Contains(
            report.Items,
            item => item.RelativePath == "b.dat"
                && item.Code == "copy.recovery.target_conflict");
        Assert.Contains(
            report.Items,
            item => item.RelativePath == "unknown.dat"
                && item.Code == "copy.recovery.target_unknown");
    }

    [Fact]
    public void RecoveryRejectsChangedSourceAndTamperedManifest()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var source = SourceEvidence(fixture, now);
        var planner = new CopyBatchPlanner();
        var manifest = planner.Compile(
            fixture.Plan,
            fixture.CopyStep.Id,
            source,
            EmptyDestination(fixture),
            100,
            2,
            now);
        var changedSource = source with
        {
            Entries =
            [
                source.Entries.Single(item => item.RelativePath == "a.dat")
                    with { LastWriteTimeUtc = now.AddSeconds(1) },
                source.Entries.Single(item => item.RelativePath == "b.dat"),
                source.Entries.Single(
                    item => item.RelativePath == "nested\\c.dat")
            ]
        };

        var report = planner.Recover(
            manifest,
            changedSource,
            EmptyDestination(fixture));

        Assert.Contains(
            report.Items,
            item => item.RelativePath == "a.dat"
                && item.Code == "copy.recovery.source_changed");
        Assert.Throws<UnauthorizedAccessException>(
            () => planner.Recover(
                manifest with { PlanHash = new string('0', 64) },
                source,
                EmptyDestination(fixture)));
    }

    [Fact]
    public void PlansFiftyThousandFilesIntoDeterministicBoundedBatches()
    {
        const int fileCount = 50_505;
        var fixture = CreateFixture(fileCount);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var entries = Enumerable.Range(0, fileCount)
            .Reverse()
            .Select(index => new RegisteredDirectoryEntryEvidence(
                $"tree\\file-{index:D6}.bin",
                1,
                FileAttributes.Normal,
                now,
                null))
            .ToArray();
        var source = new RegisteredDirectoryEvidence(
            fixture.Source.RelativePath,
            fixture.Source.IdentityToken,
            fixture.Source.MaximumBytes,
            fixture.Source.MaximumFileCount,
            fileCount,
            fileCount,
            entries);

        var manifest = new CopyBatchPlanner().Compile(
            fixture.Plan,
            fixture.CopyStep.Id,
            source,
            EmptyDestination(fixture),
            batchThresholdBytes: 4096,
            maximumFilesPerBatch: 512,
            createdAtUtc: now);

        Assert.True(CopyBatchPlanner.HasValidHash(manifest));
        Assert.Equal(fileCount, manifest.Entries.Count);
        Assert.Equal(99, manifest.Batches.Count);
        Assert.All(
            manifest.Batches,
            batch => Assert.InRange(batch.PlannedFileCount, 1, 512));
        Assert.Equal(329, manifest.Batches[^1].PlannedFileCount);
        Assert.Equal("tree\\file-000000.bin", manifest.Entries[0].RelativePath);
        Assert.Equal(
            "tree\\file-050504.bin",
            manifest.Entries[^1].RelativePath);
    }

    private static RegisteredDirectoryEvidence SourceEvidence(
        Fixture fixture,
        DateTimeOffset time) =>
        new(
            fixture.Source.RelativePath,
            fixture.Source.IdentityToken,
            fixture.Source.MaximumBytes,
            fixture.Source.MaximumFileCount,
            130,
            3,
            [
                new("b.dat", 40, FileAttributes.Normal, time, null),
                new("nested\\c.dat", 30, FileAttributes.Normal, time, null),
                new("a.dat", 60, FileAttributes.Normal, time, null)
            ]);

    private static RegisteredDirectoryEvidence EmptyDestination(Fixture fixture) =>
        new(
            fixture.Destination.RelativePath,
            fixture.Destination.IdentityToken,
            fixture.Destination.MaximumBytes,
            fixture.Destination.MaximumFileCount,
            0,
            0,
            []);

    private static Fixture CreateFixture(int targetCount = 3)
    {
        var sourceId = TestTaskId.New();
        var copyId = TestTaskId.New();
        var workload = new TestWorkload(
            Math.Max(1024, targetCount),
            4096,
            1,
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            IoAccessPattern.Sequential,
            100,
            SoftwareCacheMode.Enabled,
            WriteThroughMode.Disabled,
            false);
        var source = new TestTaskDefinition(
            sourceId,
            "source",
            TestActionKind.GenerateFile,
            new ToolId("dite.filegen"),
            workload,
            new Dictionary<string, TestParameter>
            {
                ["outputKind"] = Choice("outputKind", "directory"),
                ["profile"] = Choice("profile", "mixed"),
                ["targetCount"] = Integer("targetCount", targetCount)
            });
        var copy = new TestTaskDefinition(
            copyId,
            "copy",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = new(
                    "sourceTaskId",
                    TestParameterKind.Text,
                    sourceId.Value.ToString("D"),
                    "test.source")
            });
        var definition = new TestDefinition(
            TestDefinitionId.New(),
            "copy batches",
            "1",
            new Dictionary<string, TestParameter>(),
            [source, copy],
            [
                new("generate", sourceId, [], true),
                new("copy", copyId, ["generate"], true)
            ],
            AlgorithmConfidence.Derived);
        var systemId = SystemId.New();
        var target = new TestTarget(
            systemId,
            new StorageObjectId(
                systemId,
                StorageObjectKind.Partition,
                "copy-batch-test"),
            Path.GetTempPath(),
            long.MaxValue / 4,
            true);
        var result = new TestPlanCompiler().Compile(
            definition,
            target,
            CorrelationId.New());
        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        var copyStep = plan.Steps.Single(item => item.Id == "copy");
        var sourceDirectory = plan.Workspace.RegisteredDirectories.Single(
            item => StringComparer.OrdinalIgnoreCase.Equals(
                item.RelativePath,
                copyStep.Parameters["sourceRelativeDirectory"].SerializedValue));
        var destinationDirectory = plan.Workspace.RegisteredDirectories.Single(
            item => StringComparer.OrdinalIgnoreCase.Equals(
                item.RelativePath,
                copyStep.Parameters["destinationRelativeDirectory"].SerializedValue));
        return new(plan, copyStep, sourceDirectory, destinationDirectory);
    }

    private static TestParameter Choice(string name, string value) =>
        new(name, TestParameterKind.Choice, value, $"test.{name}");

    private static TestParameter Integer(string name, int value) =>
        new(
            name,
            TestParameterKind.Integer,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"test.{name}");

    private sealed record Fixture(
        TestPlan Plan,
        TestStep CopyStep,
        RegisteredTestDirectory Source,
        RegisteredTestDirectory Destination);
}
