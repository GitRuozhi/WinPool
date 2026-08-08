using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Testing;

namespace WinPool.Testing.Tests;

public sealed class RegisteredTestFileExecutorTests
{
    private const int BlockSize = 4096;

    [Fact]
    public async Task CreatesWritesResumesReadsHashesVerifiesAndCleansRegisteredFile()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var relativePath = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "data.bin");
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(relativePath, BlockSize * 5L + 37, "file-identity-1")],
            runId);
        var executor = new RegisteredTestFileExecutor();
        var recovery = await executor.CreateAsync(
            run,
            new CreateRegisteredTestFileRequest(
                relativePath,
                new DeterministicTestFilePattern(Seed: 123456, BlockSize)),
            CancellationToken.None);

        var partial = await executor.WriteAsync(
            run,
            new WriteRegisteredTestFileRequest(
                recovery,
                MaximumBytesThisCall: BlockSize * 2L),
            CancellationToken.None);

        Assert.Equal(
            RegisteredTestFileExecutionStatus.PartiallyCompleted,
            partial.Status);
        Assert.Equal(BlockSize * 2L, partial.Recovery.ConfirmedLength);
        Assert.Null(partial.Recovery.Sha256);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResult = await executor.WriteAsync(
            run,
            partial.Recovery,
            cancelled.Token);
        Assert.Equal(
            RegisteredTestFileExecutionStatus.Cancelled,
            cancelledResult.Status);
        Assert.Equal(partial.Recovery, cancelledResult.Recovery);

        var completed = await executor.WriteAsync(
            run,
            partial.Recovery,
            CancellationToken.None);
        Assert.Equal(RegisteredTestFileExecutionStatus.Succeeded, completed.Status);
        Assert.Equal(completed.Recovery.PlannedLength, completed.Recovery.ConfirmedLength);
        Assert.Matches("^[0-9a-f]{64}$", completed.Recovery.Sha256);

        var firstRead = await executor.ReadAsync(
            run,
            new ReadRegisteredTestFileRequest(relativePath, 17, 100),
            CancellationToken.None);
        var secondRead = await executor.ReadAsync(
            run,
            new ReadRegisteredTestFileRequest(relativePath, 17, 100),
            CancellationToken.None);
        Assert.Equal(firstRead.Data, secondRead.Data);
        Assert.Contains(firstRead.Data, value => value != 0);

        var hash = await executor.HashAsync(
            run,
            relativePath,
            CancellationToken.None);
        Assert.Equal(completed.Recovery.Sha256, hash.Sha256);
        Assert.Equal(completed.Recovery.PlannedLength, hash.BytesRead);

        foreach (var mode in Enum.GetValues<RegisteredTestFileVerificationMode>())
        {
            var verified = await executor.VerifyAsync(
                run,
                new VerifyRegisteredTestFileRequest(completed.Recovery, mode),
                CancellationToken.None);
            Assert.True(verified.IsMatch);
        }

        var absolutePath = Path.Combine(directory.Path, relativePath);
        var cleanup = await executor.CleanupAsync(
            run,
            [completed.Recovery],
            CancellationToken.None);
        Assert.Equal(RegisteredTestFileExecutionStatus.Succeeded, cleanup.Status);
        Assert.Equal([relativePath], cleanup.RemovedRelativePaths);
        Assert.False(File.Exists(absolutePath));
    }

    [Fact]
    public async Task RejectsTraversalAndUnregisteredPathsWithoutTouchingOtherFiles()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var runRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"));
        var registeredPath = Path.Combine(runRelative, "registered.bin");
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(registeredPath, BlockSize, "registered-identity")],
            runId);
        var unrelatedPath = Path.Combine(directory.Path, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedPath, "keep");
        var executor = new RegisteredTestFileExecutor();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.CreateAsync(
                run,
                new CreateRegisteredTestFileRequest(
                    Path.Combine("..", "unrelated.txt"),
                    new DeterministicTestFilePattern(1, BlockSize)),
                CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.ReadAsync(
                run,
                new ReadRegisteredTestFileRequest(
                    Path.Combine(runRelative, "not-registered.bin"),
                    0,
                    1),
                CancellationToken.None));

        var forgedCleanup = new RegisteredTestFileRecoveryEntry(
            Path.Combine("..", "unrelated.txt"),
            "registered-identity",
            BlockSize,
            BlockSize,
            1,
            BlockSize,
            new string('0', 64),
            DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.CleanupAsync(
                run,
                [forgedCleanup],
                CancellationToken.None));

        Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, registeredPath)));
    }

    [Fact]
    public async Task RefusesToOverwriteExistingUnrelatedFileAtRegisteredPath()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var relativePath = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "registered.bin");
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(relativePath, BlockSize, "file-identity")],
            runId);
        var absolutePath = Path.Combine(directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var unrelated = new byte[] { 1, 3, 3, 7 };
        await File.WriteAllBytesAsync(absolutePath, unrelated);

        var executor = new RegisteredTestFileExecutor();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.CreateAsync(
                run,
                new CreateRegisteredTestFileRequest(
                    relativePath,
                    new DeterministicTestFilePattern(7, BlockSize)),
                CancellationToken.None));

        Assert.Equal(unrelated, await File.ReadAllBytesAsync(absolutePath));
    }

    [Fact]
    public async Task CleanupRejectsModifiedRegisteredFileAndPreservesIt()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var relativePath = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "registered.bin");
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(relativePath, BlockSize * 2L, "file-identity")],
            runId);
        var executor = new RegisteredTestFileExecutor();
        var recovery = await executor.CreateAsync(
            run,
            new CreateRegisteredTestFileRequest(
                relativePath,
                new DeterministicTestFilePattern(42, BlockSize)),
            CancellationToken.None);
        var written = await executor.WriteAsync(
            run,
            recovery,
            CancellationToken.None);
        var absolutePath = Path.Combine(directory.Path, relativePath);

        await using (var stream = new FileStream(
                         absolutePath,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.Read))
        {
            stream.WriteByte(0);
        }

        var cleanup = await executor.CleanupAsync(
            run,
            [written.Recovery],
            CancellationToken.None);

        Assert.Equal(RegisteredTestFileExecutionStatus.Conflict, cleanup.Status);
        Assert.Equal([relativePath], cleanup.ConflictRelativePaths);
        Assert.Empty(cleanup.RemovedRelativePaths);
        Assert.True(File.Exists(absolutePath));
    }

    [Fact]
    public async Task RejectsSymbolicLinkInRegisteredPathWhenPlatformPermitsCreation()
    {
        using var directory = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var runRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"));
        var linkedRelative = Path.Combine(runRelative, "linked");
        var relativePath = Path.Combine(linkedRelative, "data.bin");
        var linkPath = Path.Combine(directory.Path, linkedRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, outside.Path);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(relativePath, BlockSize, "file-identity")],
            runId);
        var executor = new RegisteredTestFileExecutor();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.CreateAsync(
                run,
                new CreateRegisteredTestFileRequest(
                    relativePath,
                    new DeterministicTestFilePattern(9, BlockSize)),
                CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(outside.Path, "data.bin")));
    }

    [Fact]
    public async Task RejectsWorkspaceWhoseRegisteredFileIsOutsideRunDirectory()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var runDirectory = Path.Combine(
            directory.Path,
            "WinPoolRuns",
            runId.Value.ToString("N"));
        var outsideRunRelative = Path.Combine("other-run", "data.bin");
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(outsideRunRelative, BlockSize, "file-identity")],
            runId,
            runDirectory);
        var executor = new RegisteredTestFileExecutor();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.CreateAsync(
                run,
                new CreateRegisteredTestFileRequest(
                    outsideRunRelative,
                    new DeterministicTestFilePattern(9, BlockSize)),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExternalToolEvidenceAllowsOnlyUnchangedRegisteredFileCleanup()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var relativePath = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "external.bin");
        var fullPath = Path.Combine(directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, new byte[BlockSize]);
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(relativePath, BlockSize, "external-identity")],
            runId);
        var executor = new RegisteredTestFileExecutor();

        var evidence = await executor.CaptureExternalEvidenceAsync(
            run,
            relativePath,
            requirePlannedLength: true,
            CancellationToken.None);
        var cleanup = await executor.CleanupExternalEvidenceAsync(
            run,
            [evidence],
            CancellationToken.None);

        Assert.Equal(RegisteredTestFileExecutionStatus.Succeeded, cleanup.Status);
        Assert.Equal([relativePath], cleanup.RemovedRelativePaths);
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public async Task ExternalToolEvidenceConflictPreservesChangedFile()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var relativePath = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "external.bin");
        var fullPath = Path.Combine(directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, new byte[BlockSize]);
        var run = CreateAuthorizedRun(
            directory.Path,
            [new RegisteredTestFile(relativePath, BlockSize, "external-identity")],
            runId);
        var executor = new RegisteredTestFileExecutor();
        var evidence = await executor.CaptureExternalEvidenceAsync(
            run,
            relativePath,
            requirePlannedLength: true,
            CancellationToken.None);
        var changed = new byte[BlockSize];
        changed[0] = 1;
        await File.WriteAllBytesAsync(fullPath, changed);

        var cleanup = await executor.CleanupExternalEvidenceAsync(
            run,
            [evidence],
            CancellationToken.None);

        Assert.Equal(RegisteredTestFileExecutionStatus.Conflict, cleanup.Status);
        Assert.Equal([relativePath], cleanup.ConflictRelativePaths);
        Assert.True(File.Exists(fullPath));
    }

    [Theory]
    [InlineData(RegisteredTestFileVerificationMode.Metadata)]
    [InlineData(RegisteredTestFileVerificationMode.SampledContent)]
    [InlineData(RegisteredTestFileVerificationMode.FullHash)]
    public async Task ExternalCopyPairSupportsTypedVerificationModes(
        RegisteredTestFileVerificationMode mode)
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var sourceRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "source.bin");
        var destinationRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "copies",
            "destination.bin");
        var content = Enumerable.Range(0, BlockSize * 40)
            .Select(index => (byte)(index * 31))
            .ToArray();
        var sourcePath = Path.Combine(directory.Path, sourceRelative);
        var destinationPath = Path.Combine(directory.Path, destinationRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(sourcePath, content);
        await File.WriteAllBytesAsync(destinationPath, content);
        File.SetLastWriteTimeUtc(
            destinationPath,
            File.GetLastWriteTimeUtc(sourcePath));
        File.SetAttributes(
            destinationPath,
            File.GetAttributes(sourcePath));
        var run = CreateAuthorizedRun(
            directory.Path,
            [
                new(sourceRelative, content.Length, "source-identity"),
                new(destinationRelative, content.Length, "destination-identity")
            ],
            runId);
        var executor = new RegisteredTestFileExecutor();

        var verified = await executor.VerifyExternalPairAsync(
            run,
            new(
                sourceRelative,
                destinationRelative,
                mode,
                SampleCount: 12),
            CancellationToken.None);

        Assert.True(verified.IsMatch);
        if (mode is RegisteredTestFileVerificationMode.FullHash)
        {
            Assert.Equal(verified.SourceSha256, verified.DestinationSha256);
            Assert.Equal(content.Length * 2L, verified.VerifiedBytes);
        }
    }

    [Fact]
    public async Task ExternalCopySampleVerificationDetectsChangedSampledBoundary()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var sourceRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "source.bin");
        var destinationRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "copies",
            "destination.bin");
        var content = new byte[BlockSize * 40];
        var sourcePath = Path.Combine(directory.Path, sourceRelative);
        var destinationPath = Path.Combine(directory.Path, destinationRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(sourcePath, content);
        content[0] = 1;
        await File.WriteAllBytesAsync(destinationPath, content);
        var run = CreateAuthorizedRun(
            directory.Path,
            [
                new(sourceRelative, content.Length, "source-identity"),
                new(destinationRelative, content.Length, "destination-identity")
            ],
            runId);

        var verified = await new RegisteredTestFileExecutor()
            .VerifyExternalPairAsync(
                run,
                new(
                    sourceRelative,
                    destinationRelative,
                    RegisteredTestFileVerificationMode.SampledContent),
                CancellationToken.None);

        Assert.False(verified.IsMatch);
        Assert.Equal(0, verified.FirstMismatchOffset);
    }

    [Fact]
    public async Task ExternalPairRejectsPatternReplayWithoutGeneratorRecovery()
    {
        using var directory = TemporaryDirectory.Create();
        var runId = TestRunId.New();
        var sourceRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "source.bin");
        var destinationRelative = Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "destination.bin");
        var run = CreateAuthorizedRun(
            directory.Path,
            [
                new(sourceRelative, BlockSize, "source-identity"),
                new(destinationRelative, BlockSize, "destination-identity")
            ],
            runId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RegisteredTestFileExecutor().VerifyExternalPairAsync(
                run,
                new(
                    sourceRelative,
                    destinationRelative,
                    RegisteredTestFileVerificationMode.PatternReplay),
                CancellationToken.None));
    }

    private static AuthorizedTestRun CreateAuthorizedRun(
        string root,
        IReadOnlyList<RegisteredTestFile> registeredFiles,
        TestRunId? specifiedRunId = null,
        string? specifiedRunDirectory = null)
    {
        var runId = specifiedRunId ?? TestRunId.New();
        var runDirectory = specifiedRunDirectory
                           ?? Path.Combine(
                               root,
                               "WinPoolRuns",
                               runId.Value.ToString("N"));
        var totalLength = registeredFiles.Sum(file => file.PlannedLength);
        var systemId = SystemId.New();
        var workspace = new TestWorkspacePlan(
            Path.GetFullPath(root),
            Path.GetFullPath(runDirectory),
            registeredFiles,
            totalLength,
            TestWorkspaceCleanupPolicy.RemoveVerifiedRegisteredFiles,
            DateTimeOffset.UtcNow.AddHours(1));
        var target = new TestTarget(
            systemId,
            new StorageObjectId(
                systemId,
                StorageObjectKind.Partition,
                "test-volume"),
            Path.GetFullPath(root),
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
            totalLength,
            RiskLevel.R2RecoverableFileWrite,
            TestPlanCompiler.Algorithm,
            DateTimeOffset.UtcNow,
            new string('a', 64));
        return new AuthorizedTestRun(
            plan,
            new WinPool.Application.AuthorizedTestWorkspace(workspace),
            []);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinPool.Testing.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
