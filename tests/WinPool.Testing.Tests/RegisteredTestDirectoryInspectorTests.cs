using WinPool.Application;
using WinPool.Domain;
using WinPool.Testing;
using RiskLevel = WinPool.Execution.RiskLevel;

namespace WinPool.Testing.Tests;

public sealed class RegisteredTestDirectoryInspectorTests
{
    [Theory]
    [InlineData(RegisteredTestFileVerificationMode.Metadata)]
    [InlineData(RegisteredTestFileVerificationMode.SampledContent)]
    [InlineData(RegisteredTestFileVerificationMode.FullHash)]
    public async Task VerifiesRegisteredDirectoryPairWithinQuota(
        RegisteredTestFileVerificationMode mode)
    {
        using var fixture = DirectoryFixture.Create(
            maximumBytes: 1024 * 1024,
            maximumFiles: 10);
        await fixture.WriteMatchingFileAsync("one.bin", [1, 2, 3, 4]);
        await fixture.WriteMatchingFileAsync(
            Path.Combine("nested", "two.bin"),
            Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray());

        var result = await new RegisteredTestDirectoryInspector()
            .VerifyPairAsync(
                fixture.Run,
                new(
                    fixture.SourceRelative,
                    fixture.DestinationRelative,
                    mode,
                    SampleCount: 8),
                CancellationToken.None);

        Assert.True(result.IsMatch);
        Assert.Equal(2, result.ComparedFileCount);
        if (mode is RegisteredTestFileVerificationMode.FullHash
            or RegisteredTestFileVerificationMode.SampledContent)
        {
            Assert.True(result.ComparedBytes > 0);
        }
    }

    [Fact]
    public async Task FullHashDetectsChangedFileAndReportsStableRelativePath()
    {
        using var fixture = DirectoryFixture.Create(
            maximumBytes: 1024 * 1024,
            maximumFiles: 10);
        await fixture.WriteMatchingFileAsync(
            Path.Combine("nested", "payload.bin"),
            new byte[8192]);
        var changed = new byte[8192];
        changed[^1] = 1;
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.DestinationPath, "nested", "payload.bin"),
            changed);

        var result = await new RegisteredTestDirectoryInspector()
            .VerifyPairAsync(
                fixture.Run,
                new(
                    fixture.SourceRelative,
                    fixture.DestinationRelative,
                    RegisteredTestFileVerificationMode.FullHash),
                CancellationToken.None);

        Assert.False(result.IsMatch);
        Assert.Equal(
            Path.Combine("nested", "payload.bin"),
            result.FirstMismatchRelativePath);
    }

    [Fact]
    public async Task CaptureRejectsDirectoryThatExceedsRegisteredFileCount()
    {
        using var fixture = DirectoryFixture.Create(
            maximumBytes: 1024 * 1024,
            maximumFiles: 1);
        Directory.CreateDirectory(fixture.SourcePath);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourcePath, "one.txt"),
            "one");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourcePath, "two.txt"),
            "two");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new RegisteredTestDirectoryInspector().CaptureAsync(
                fixture.Run,
                fixture.SourceRelative,
                includeHashes: false,
                CancellationToken.None));
    }

    private sealed class DirectoryFixture : IDisposable
    {
        private DirectoryFixture(
            string root,
            string sourceRelative,
            string destinationRelative,
            AuthorizedTestRun run)
        {
            Root = root;
            SourceRelative = sourceRelative;
            DestinationRelative = destinationRelative;
            Run = run;
        }

        public string Root { get; }

        public string SourceRelative { get; }

        public string DestinationRelative { get; }

        public string SourcePath => Path.Combine(Root, SourceRelative);

        public string DestinationPath => Path.Combine(Root, DestinationRelative);

        public AuthorizedTestRun Run { get; }

        public static DirectoryFixture Create(
            long maximumBytes,
            int maximumFiles)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WinPool.DirectoryInspector.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var runId = TestRunId.New();
            var runRelative = Path.Combine(
                "WinPoolRuns",
                runId.Value.ToString("N"));
            var sourceRelative = Path.Combine(runRelative, "generated", "source");
            var destinationRelative = Path.Combine(runRelative, "copies", "destination");
            var directories = new[]
            {
                new RegisteredTestDirectory(
                    sourceRelative,
                    maximumBytes,
                    maximumFiles,
                    "source-identity"),
                new RegisteredTestDirectory(
                    destinationRelative,
                    maximumBytes,
                    maximumFiles,
                    "destination-identity")
            };
            var workspace = new TestWorkspacePlan(
                root,
                Path.Combine(root, runRelative),
                [],
                checked(maximumBytes * 2),
                TestWorkspaceCleanupPolicy.KeepAll,
                DateTimeOffset.UtcNow.AddHours(1))
            {
                RegisteredDirectories = directories
            };
            var systemId = SystemId.New();
            var target = new TestTarget(
                systemId,
                new StorageObjectId(
                    systemId,
                    StorageObjectKind.Partition,
                    "directory-test-volume"),
                root,
                long.MaxValue,
                IsWriteAllowed: true);
            var plan = new TestPlan(
                runId,
                TestDefinitionId.New(),
                "test",
                target,
                workspace,
                [],
                [],
                [],
                checked(maximumBytes * 2),
                RiskLevel.R2RecoverableFileWrite,
                TestPlanCompiler.Algorithm,
                DateTimeOffset.UtcNow,
                new string('a', 64));
            return new(
                root,
                sourceRelative,
                destinationRelative,
                new AuthorizedTestRun(
                    plan,
                    new AuthorizedTestWorkspace(workspace),
                    []));
        }

        public async Task WriteMatchingFileAsync(
            string relativePath,
            byte[] content)
        {
            var source = Path.Combine(SourcePath, relativePath);
            var destination = Path.Combine(DestinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(source, content);
            await File.WriteAllBytesAsync(destination, content);
            File.SetAttributes(destination, File.GetAttributes(source));
            File.SetLastWriteTimeUtc(
                destination,
                File.GetLastWriteTimeUtc(source));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
