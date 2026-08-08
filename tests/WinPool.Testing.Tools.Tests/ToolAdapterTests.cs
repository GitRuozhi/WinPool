using WinPool.Application;
using WinPool.Domain;
using WinPool.Testing.Tools;

namespace WinPool.Testing.Tools.Tests;

public sealed class ToolAdapterTests
{
    [Theory]
    [InlineData(SoftwareCacheMode.Enabled, WriteThroughMode.Disabled, "-Sb")]
    [InlineData(SoftwareCacheMode.Disabled, WriteThroughMode.Disabled, "-Su")]
    [InlineData(SoftwareCacheMode.Enabled, WriteThroughMode.Enabled, "-Sw")]
    [InlineData(SoftwareCacheMode.Disabled, WriteThroughMode.Enabled, "-Sh")]
    public void DiskSpdMapsEveryCacheCombinationExactly(
        SoftwareCacheMode softwareCache,
        WriteThroughMode writeThrough,
        string expectedFlag)
    {
        using var workspace = TemporaryWorkspace.Create("run/test.bin");
        var adapter = new DiskSpdAdapter(
            Path.Combine(workspace.Root, "diskspd.exe"));
        var step = CreateIoStep(
            new ToolId("microsoft.diskspd"),
            softwareCache,
            writeThrough,
            "run/test.bin");

        var result = adapter.BuildInvocation(
            step,
            workspace.Authorization,
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Contains(expectedFlag, result.Value.Arguments);
        Assert.Single(
            result.Value.Arguments,
            argument => argument is "-Sb" or "-Su" or "-Sw" or "-Sh");
        Assert.DoesNotContain("cmd.exe", result.Value.ExecutablePath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.Root, "run", "test.bin")),
            result.Value.Arguments[^1]);
    }

    [Fact]
    public void DiskSpdRequiresExplicitRegisteredTargetRelativePath()
    {
        using var workspace = TemporaryWorkspace.Create("run/test.bin");
        var adapter = new DiskSpdAdapter(
            Path.Combine(workspace.Root, "diskspd.exe"));
        var step = CreateIoStep(
            new ToolId("microsoft.diskspd"),
            SoftwareCacheMode.Enabled,
            WriteThroughMode.Disabled,
            null);

        var result = adapter.BuildInvocation(
            step,
            workspace.Authorization,
            CorrelationId.New());

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal(
            "tool.adapter.parameter.target_relative_path_required",
            Assert.Single(result.Messages).Code);
    }

    [Fact]
    public void RegisteredFileCannotAuthorizeSiblingOrTraversalPath()
    {
        using var workspace = TemporaryWorkspace.Create("run/test.bin");
        var adapter = new DiskSpdAdapter(
            Path.Combine(workspace.Root, "diskspd.exe"));
        var sibling = CreateIoStep(
            new ToolId("microsoft.diskspd"),
            SoftwareCacheMode.Enabled,
            WriteThroughMode.Disabled,
            "run/other.bin");
        var traversal = CreateIoStep(
            new ToolId("microsoft.diskspd"),
            SoftwareCacheMode.Enabled,
            WriteThroughMode.Disabled,
            "../escape.bin");

        var siblingResult = adapter.BuildInvocation(
            sibling,
            workspace.Authorization,
            CorrelationId.New());
        var traversalResult = adapter.BuildInvocation(
            traversal,
            workspace.Authorization,
            CorrelationId.New());

        Assert.Equal(
            "tool.adapter.path.not_registered",
            Assert.Single(siblingResult.Messages).Code);
        Assert.Equal(
            "tool.adapter.path.invalid",
            Assert.Single(traversalResult.Messages).Code);
    }

    [Fact]
    public void ExistingReparsePointInTargetChainIsRejectedWhenSupported()
    {
        using var workspace = TemporaryWorkspace.Create("run/link/test.bin");
        var outside = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                "WinPool.Testing.Tools.Tests.Outside",
                Guid.NewGuid().ToString("N")));
        var link = Path.Combine(workspace.Root, "run", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside.FullName);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                    or IOException
                    or PlatformNotSupportedException)
            {
                return;
            }

            var adapter = new DiskSpdAdapter(
                Path.Combine(workspace.Root, "diskspd.exe"));
            var result = adapter.BuildInvocation(
                CreateIoStep(
                    new ToolId("microsoft.diskspd"),
                    SoftwareCacheMode.Enabled,
                    WriteThroughMode.Disabled,
                    "run/link/test.bin"),
                workspace.Authorization,
                CorrelationId.New());

            Assert.Equal(ApplicationStatus.Rejected, result.Status);
            Assert.Equal(
                "tool.adapter.path.reparse_point",
                Assert.Single(result.Messages).Code);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (outside.Exists)
            {
                outside.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void FioBuildsJsonPlusArgumentsWithoutShell()
    {
        using var workspace = TemporaryWorkspace.Create("run/test.bin");
        var adapter = new FioAdapter(Path.Combine(workspace.Root, "fio.exe"));
        var step = CreateIoStep(
            new ToolId("fio"),
            SoftwareCacheMode.Disabled,
            WriteThroughMode.Enabled,
            "run/test.bin",
            accessPattern: IoAccessPattern.Mixed,
            writePercentage: 30,
            cooldown: TimeSpan.Zero);

        var result = adapter.BuildInvocation(
            step,
            workspace.Authorization,
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Contains("--output-format=json+", result.Value.Arguments);
        Assert.Contains("--rw=randrw", result.Value.Arguments);
        Assert.Contains("--rwmixwrite=30", result.Value.Arguments);
        Assert.Contains("--direct=1", result.Value.Arguments);
        Assert.Contains("--sync=1", result.Value.Arguments);
        Assert.Contains("--eta=always", result.Value.Arguments);
        Assert.Contains("--eta-interval=1s", result.Value.Arguments);
        Assert.All(
            result.Value.Arguments,
            argument => Assert.DoesNotContain(" /c ", argument, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoboCopyUsesOnlyTwoRegisteredLiteralFilesAndReviewedFlags()
    {
        using var workspace = TemporaryWorkspace.Create(
            "run/source/payload.bin",
            "run/destination/payload.bin");
        var adapter = new RoboCopyAdapter(
            Path.Combine(workspace.Root, "robocopy.exe"));
        var parameters = new Dictionary<string, TestParameter>
        {
            ["sourceRelativePath"] = Text(
                "sourceRelativePath",
                "run/source/payload.bin"),
            ["destinationRelativePath"] = Text(
                "destinationRelativePath",
                "run/destination/payload.bin"),
            ["copyMode"] = new(
                "copyMode",
                TestParameterKind.Choice,
                "data",
                "test.copy_mode"),
            ["useBuffered"] = new(
                "useBuffered",
                TestParameterKind.Boolean,
                "false",
                "test.use_buffered"),
            ["threadCount"] = new(
                "threadCount",
                TestParameterKind.Integer,
                "8",
                "test.thread_count")
        };
        var step = new TestStep(
            "copy",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            parameters,
            [],
            true);

        var result = adapter.BuildInvocation(
            step,
            workspace.Authorization,
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Contains("payload.bin", result.Value.Arguments);
        Assert.Contains("/COPY:D", result.Value.Arguments);
        Assert.Contains("/XJ", result.Value.Arguments);
        Assert.Contains("/J", result.Value.Arguments);
        Assert.Contains("/MT:8", result.Value.Arguments);
        Assert.DoesNotContain("/NP", result.Value.Arguments);
        Assert.DoesNotContain("/MIR", result.Value.Arguments);
        Assert.DoesNotContain("/COPYALL", result.Value.Arguments);
    }

    [Fact]
    public void RoboCopyDirectoryModeUsesOnlyRegisteredRootsAndNoMirrorDeletion()
    {
        using var workspace = TemporaryWorkspace.CreateDirectories(
            "run/generated/source",
            "run/copies/destination");
        var adapter = new RoboCopyAdapter(
            Path.Combine(workspace.Root, "robocopy.exe"));
        var step = new TestStep(
            "copy-directory",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceRelativeDirectory"] = Text(
                    "sourceRelativeDirectory",
                    "run/generated/source"),
                ["destinationRelativeDirectory"] = Text(
                    "destinationRelativeDirectory",
                    "run/copies/destination")
            },
            [],
            true);

        var result = adapter.BuildInvocation(
            step,
            workspace.Authorization,
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        Assert.Equal(
            Path.Combine(workspace.Root, "run", "generated", "source"),
            result.Value!.Arguments[0]);
        Assert.Equal(
            Path.Combine(workspace.Root, "run", "copies", "destination"),
            result.Value.Arguments[1]);
        Assert.Contains("/E", result.Value.Arguments);
        Assert.Contains("/XJ", result.Value.Arguments);
        Assert.Contains("/J", result.Value.Arguments);
        Assert.DoesNotContain("/MIR", result.Value.Arguments);
        Assert.DoesNotContain("/MOVE", result.Value.Arguments);
        Assert.DoesNotContain("/PURGE", result.Value.Arguments);

        var entry = adapter.BuildDirectoryEntryInvocation(
            step,
            workspace.Authorization,
            Path.Combine("nested", "payload.bin"),
            CorrelationId.New());
        Assert.True(entry.IsSuccess);
        Assert.Equal(
            Path.Combine(
                workspace.Root,
                "run",
                "generated",
                "source",
                "nested"),
            entry.Value!.Arguments[0]);
        Assert.Equal(
            Path.Combine(
                workspace.Root,
                "run",
                "copies",
                "destination",
                "nested"),
            entry.Value.Arguments[1]);
        Assert.Equal("payload.bin", entry.Value.Arguments[2]);
        Assert.DoesNotContain("/E", entry.Value.Arguments);
        Assert.Contains("/XJ", entry.Value.Arguments);

        var traversal = adapter.BuildDirectoryEntryInvocation(
            step,
            workspace.Authorization,
            Path.Combine("..", "escaped.bin"),
            CorrelationId.New());
        Assert.False(traversal.IsSuccess);
        Assert.Equal(
            "tool.adapter.copy.directory_entry_path_invalid",
            Assert.Single(traversal.Messages).Code);

        var now = DateTimeOffset.UtcNow;
        var runId = TestRunId.New();
        var manifestMaterial = new CopyBatchManifest(
            runId,
            step.Id,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            1024,
            10,
            [
                new(0, 1, Path.Combine("nested", "already.bin"), 100, 1, FileAttributes.Normal, null),
                new(1, 1, Path.Combine("nested", "payload.bin"), 200, 2, FileAttributes.Normal, null)
            ],
            [new(1, 300, 2)],
            new(
                "ALG-COPY-BATCH-TEST",
                "1.0.0",
                AlgorithmConfidence.Derived,
                "test"),
            now,
            string.Empty);
        var manifest = manifestMaterial with
        {
            ManifestHash = CopyBatchManifestHash.Compute(manifestMaterial)
        };
        var tool = new ToolState(
            new ToolId(ToolProcessExitPolicy.RoboCopyToolId),
            ToolAvailability.Available,
            Path.Combine(workspace.Root, "robocopy.exe"),
            ToolPathSource.WindowsComponent,
            null,
            null,
            null,
            ToolCapabilities.FileCopy,
            false);
        var groups = new CopyBatchInvocationPlanner().Build(
            manifest,
            [
                new(runId, step.Id, 0, CopyBatchEntryState.Completed, 1, 1, null, now),
                new(runId, step.Id, 1, CopyBatchEntryState.Pending, 0, null, null, now)
            ],
            step,
            workspace.Authorization,
            tool,
            adapter,
            CorrelationId.New());

        var remaining = Assert.Single(Assert.Single(groups).Items);
        Assert.Equal(1, remaining.Entry.Ordinal);
        Assert.Equal(step.Id, remaining.Request.StepId);
        Assert.Equal("payload.bin", remaining.Request.Invocation.Arguments[2]);
    }

    [Fact]
    public void DiteFileGenBuildsOnlyNonInteractiveQuotaBoundDirectoryInvocation()
    {
        using var workspace = TemporaryWorkspace.CreateDirectories(
            "run/generated/mixed");
        var adapter = new DiteFileGenAdapter(
            Path.Combine(workspace.Root, "Dite.exe"));
        var step = new TestStep(
            "generate-mixed",
            TestActionKind.GenerateFile,
            new ToolId("dite.filegen"),
            new TestWorkload(
                1024 * 1024 + 100,
                4096,
                1,
                1,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(1),
                TimeSpan.Zero,
                IoAccessPattern.Sequential,
                100,
                SoftwareCacheMode.Enabled,
                WriteThroughMode.Disabled,
                false),
            new Dictionary<string, TestParameter>
            {
                ["targetRelativeDirectory"] = Text(
                    "targetRelativeDirectory",
                    "run/generated/mixed"),
                ["profile"] = new(
                    "profile",
                    TestParameterKind.Choice,
                    "mixed",
                    "test.profile"),
                ["totalMiB"] = new(
                    "totalMiB",
                    TestParameterKind.Integer,
                    "1",
                    "test.total_mib"),
                ["targetCount"] = new(
                    "targetCount",
                    TestParameterKind.Integer,
                    "10",
                    "test.target_count")
            },
            [],
            true);

        var result = adapter.BuildInvocation(
            step,
            workspace.Authorization,
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        Assert.Equal("Dite.exe", Path.GetFileName(result.Value!.ExecutablePath));
        Assert.Equal("--filegen-output", result.Value.Arguments[0]);
        Assert.Contains("--filegen-profile", result.Value.Arguments);
        Assert.Contains("mixed", result.Value.Arguments);
        Assert.Contains("--filegen-target-count", result.Value.Arguments);
        Assert.Contains("--filegen-identity", result.Value.Arguments);
        Assert.Contains("--filegen-resume", result.Value.Arguments);
        Assert.Contains("--no-pause", result.Value.Arguments);
        Assert.DoesNotContain("--preset", result.Value.Arguments);
        Assert.DoesNotContain("--disk", result.Value.Arguments);
    }

    [Fact]
    public void RamMapMapsOnlyTypedModeToImmutableFixedWhitelist()
    {
        using var workspace = TemporaryWorkspace.Create("run/test.bin");
        var adapter = new RamMapAdapter(
            Path.Combine(workspace.Root, "RAMMap64.exe"));
        var action = new AuthorizedSystemSupportAction(
            new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList),
            "plan-hash",
            DateTimeOffset.UtcNow.AddMinutes(5));

        var result = adapter.BuildInvocation(action, CorrelationId.New());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(["-Es", "-Et"], result.Value.Arguments);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)result.Value.Arguments).Add("-Ew"));
    }

    [Fact]
    public void RamMapRejectsEnumValueOutsideWhitelistAndExpiredAuthorization()
    {
        using var workspace = TemporaryWorkspace.Create("run/test.bin");
        var adapter = new RamMapAdapter(
            Path.Combine(workspace.Root, "RAMMap.exe"));
        var unknownMode = new AuthorizedSystemSupportAction(
            new ClearSystemFileCacheAction((RamMapCacheClearMode)999),
            "plan-hash",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var expired = new AuthorizedSystemSupportAction(
            new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList),
            "plan-hash",
            DateTimeOffset.UtcNow.AddSeconds(-1));

        var unknownResult = adapter.BuildInvocation(
            unknownMode,
            CorrelationId.New());
        var expiredResult = adapter.BuildInvocation(expired, CorrelationId.New());

        Assert.Equal(
            "rammap.mode.not_whitelisted",
            Assert.Single(unknownResult.Messages).Code);
        Assert.Equal(
            "rammap.authorization.expired",
            Assert.Single(expiredResult.Messages).Code);
    }

    private static TestStep CreateIoStep(
        ToolId toolId,
        SoftwareCacheMode softwareCache,
        WriteThroughMode writeThrough,
        string? targetRelativePath,
        IoAccessPattern accessPattern = IoAccessPattern.Random,
        int writePercentage = 50,
        TimeSpan? cooldown = null)
    {
        var parameters = new Dictionary<string, TestParameter>();
        if (targetRelativePath is not null)
        {
            parameters["targetRelativePath"] = Text(
                "targetRelativePath",
                targetRelativePath);
        }

        return new TestStep(
            "io",
            TestActionKind.RunIo,
            toolId,
            new TestWorkload(
                1024 * 1024,
                4096,
                2,
                4,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                cooldown ?? TimeSpan.FromSeconds(1),
                accessPattern,
                writePercentage,
                softwareCache,
                writeThrough,
                true),
            parameters,
            [],
            true);
    }

    private static TestParameter Text(string key, string value) =>
        new(key, TestParameterKind.Text, value, $"test.{key}");

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(
            string root,
            AuthorizedTestWorkspace authorization)
        {
            Root = root;
            Authorization = authorization;
        }

        public string Root { get; }

        public AuthorizedTestWorkspace Authorization { get; }

        public static TemporaryWorkspace Create(params string[] registeredPaths)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Testing.Tools.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var runDirectory = Path.Combine(root, "run");
            var plan = new TestWorkspacePlan(
                root,
                runDirectory,
                registeredPaths
                    .Select(path => new RegisteredTestFile(path, 1024, "identity"))
                    .ToArray(),
                1024 * registeredPaths.Length,
                TestWorkspaceCleanupPolicy.RemoveVerifiedRegisteredFiles,
                DateTimeOffset.UtcNow.AddHours(1));
            return new TemporaryWorkspace(
                root,
                new AuthorizedTestWorkspace(plan));
        }

        public static TemporaryWorkspace CreateDirectories(
            params string[] registeredPaths)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Testing.Tools.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var runDirectory = Path.Combine(root, "run");
            var directories = registeredPaths
                .Select(path => new RegisteredTestDirectory(
                    path,
                    2 * 1024 * 1024,
                    100,
                    new string('a', 64)))
                .ToArray();
            var plan = new TestWorkspacePlan(
                root,
                runDirectory,
                [],
                directories.Sum(item => item.MaximumBytes),
                TestWorkspaceCleanupPolicy.RemoveVerifiedRegisteredFiles,
                DateTimeOffset.UtcNow.AddHours(1))
            {
                RegisteredDirectories = directories
            };
            return new TemporaryWorkspace(
                root,
                new AuthorizedTestWorkspace(plan));
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
