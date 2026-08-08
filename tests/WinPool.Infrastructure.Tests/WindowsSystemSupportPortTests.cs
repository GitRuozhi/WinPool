using System.Security.Cryptography;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.ToolManagement;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsSystemSupportPortTests
{
    [Fact]
    public async Task MsiPortRehashesPinnedStagingFileAndUsesFixedVisibleInstallerArguments()
    {
        using var workspace = new TemporaryWorkspace();
        byte[] bytes = [4, 2, 4, 2];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var staging = Path.Combine(workspace.Root, "tool-downloads");
        Directory.CreateDirectory(staging);
        var package = Path.Combine(staging, $"{hash.ToLowerInvariant()}.msi");
        File.WriteAllBytes(package, bytes);
        var descriptor = new ToolDescriptor(
            KnownToolIds.Fio,
            "fio",
            "test",
            ["fio.exe"],
            new Uri("https://github.com/axboe/fio"),
            new Uri("https://github.com/axboe/fio/releases/download/test/fio.msi"),
            ToolInstallerKind.Msi,
            ToolVersionProbeKind.FileVersionMetadata,
            new ToolVersionPolicy(new Version(3, 31), new Version(4, 0)),
            ToolCapabilities.SequentialIo,
            false,
            true,
            hash);
        var runner = new RecordingCommandRunner(new WindowsCommandResult(0, "", ""));
        var port = new WindowsMsiToolInstallPort(
            workspace.Root,
            new ToolCatalog([descriptor]),
            runner,
            Path.Combine(workspace.Root, "windows"));

        var evidence = await port.InstallAsync(
            new MsiToolInstallSnapshot(
                KnownToolIds.Fio,
                Path.Combine("tool-downloads", $"{hash.ToLowerInvariant()}.msi"),
                hash),
            CancellationToken.None);

        Assert.Equal(0, evidence.ExitCode);
        var command = Assert.Single(runner.Commands);
        Assert.EndsWith(Path.Combine("System32", "msiexec.exe"), command.ExecutablePath);
        Assert.Equal(["/i", package, "/passive", "/norestart"], command.Arguments);
    }

    [Fact]
    public void VolumeIdentityProbeResolvesCurrentSystemVolumeWithoutMutation()
    {
        var systemId = SystemId.New();
        var volumeId = new StorageObjectId(
            systemId,
            StorageObjectKind.Partition,
            "system-volume");
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;

        var snapshot = WindowsVolumeIdentityProbe.Resolve(volumeId, root);

        Assert.NotNull(snapshot);
        Assert.Equal(volumeId, snapshot.VolumeId);
        Assert.StartsWith(
            @"\\?\VOLUME{",
            snapshot.StableIdentity,
            StringComparison.Ordinal);
        Assert.True(
            StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(root).TrimEnd('\\') + "\\",
                snapshot.DisplayIdentity));
    }

    [Fact]
    public async Task TemporaryFilePortRemovesOnlyUnchangedReviewedFile()
    {
        using var workspace = new TemporaryWorkspace();
        var roots = workspace.CreateRoots();
        var allowedPath = Path.Combine(roots.WinPoolTemporaryDirectory, "run", "evidence.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(allowedPath)!);
        await File.WriteAllTextAsync(allowedPath, "registered");
        var port = new WindowsTemporaryFileCleanupPort(roots);

        var candidates = await port.ScanAsync(
            [TemporaryFileScope.WinPoolTemporaryFiles],
            CancellationToken.None);
        var candidate = Assert.Single(candidates);
        var result = await port.CleanAsync([candidate], CancellationToken.None);

        Assert.False(File.Exists(allowedPath));
        Assert.Equal(TemporaryCleanupItemStatus.Removed, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task TemporaryFilePortPreservesFileWhoseIdentityChangedAfterReview()
    {
        using var workspace = new TemporaryWorkspace();
        var roots = workspace.CreateRoots();
        var path = Path.Combine(roots.CurrentUserTemporaryDirectory, "changing.tmp");
        await File.WriteAllTextAsync(path, "before");
        var port = new WindowsTemporaryFileCleanupPort(roots);
        var candidate = Assert.Single(
            await port.ScanAsync(
                [TemporaryFileScope.CurrentUserTemporaryFiles],
                CancellationToken.None));

        await File.AppendAllTextAsync(path, "-after");
        var result = await port.CleanAsync([candidate], CancellationToken.None);

        Assert.True(File.Exists(path));
        var item = Assert.Single(result.Items);
        Assert.Equal(TemporaryCleanupItemStatus.Skipped, item.Status);
        Assert.Equal("temporary-cleanup.identity.changed", item.Code);
    }

    [Fact]
    public async Task TemporaryFilePortRechecksWindowsProtectionImmediatelyBeforeRemoval()
    {
        using var workspace = new TemporaryWorkspace();
        var roots = workspace.CreateRoots();
        var path = Path.Combine(roots.CurrentUserTemporaryDirectory, "protected-late.tmp");
        await File.WriteAllTextAsync(path, "evidence");
        var protection = new ToggleProtectionDetector();
        var port = new WindowsTemporaryFileCleanupPort(roots, protection);
        var candidate = Assert.Single(
            await port.ScanAsync(
                [TemporaryFileScope.CurrentUserTemporaryFiles],
                CancellationToken.None));

        protection.IsProtectedNow = true;
        var result = await port.CleanAsync([candidate], CancellationToken.None);

        Assert.True(File.Exists(path));
        var item = Assert.Single(result.Items);
        Assert.Equal(TemporaryCleanupItemStatus.Skipped, item.Status);
        Assert.Equal("temporary-cleanup.windows-resource-protected", item.Code);
    }

    [Fact]
    public async Task TemporaryFilePortRejectsAncestorReparseIntroducedAfterReview()
    {
        using var workspace = new TemporaryWorkspace();
        var roots = workspace.CreateRoots();
        var reviewedDirectory = Path.Combine(roots.CurrentUserTemporaryDirectory, "reviewed");
        Directory.CreateDirectory(reviewedDirectory);
        var reviewedPath = Path.Combine(reviewedDirectory, "candidate.tmp");
        await File.WriteAllTextAsync(reviewedPath, "same-content");
        var timestamp = File.GetLastWriteTimeUtc(reviewedPath);
        var port = new WindowsTemporaryFileCleanupPort(roots);
        var candidate = Assert.Single(
            await port.ScanAsync(
                [TemporaryFileScope.CurrentUserTemporaryFiles],
                CancellationToken.None));

        var retainedDirectory = reviewedDirectory + "-original";
        Directory.Move(reviewedDirectory, retainedDirectory);
        var redirectedDirectory = Path.Combine(workspace.Root, "redirected-target");
        Directory.CreateDirectory(redirectedDirectory);
        var redirectedPath = Path.Combine(redirectedDirectory, "candidate.tmp");
        await File.WriteAllTextAsync(redirectedPath, "same-content");
        File.SetLastWriteTimeUtc(redirectedPath, timestamp);
        try
        {
            Directory.CreateSymbolicLink(reviewedDirectory, redirectedDirectory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
        {
            return;
        }

        var result = await port.CleanAsync([candidate], CancellationToken.None);

        Assert.True(File.Exists(redirectedPath));
        var item = Assert.Single(result.Items);
        Assert.Equal(TemporaryCleanupItemStatus.Skipped, item.Status);
        Assert.Equal("temporary-cleanup.reparse-point", item.Code);
    }

    [Fact]
    public async Task SchedulingPortExposesOnlyRegisteredTestProcesses()
    {
        var rejected = new WindowsTestProcessSchedulingPort(_ => false);
        Assert.Null(await rejected.CaptureAsync(Environment.ProcessId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rejected.ApplyAsync(
                Environment.ProcessId,
                TestProcessPriority.Normal,
                [0],
                CancellationToken.None));

        var accepted = new WindowsTestProcessSchedulingPort(
            processId => processId == Environment.ProcessId);
        var snapshot = await accepted.CaptureAsync(Environment.ProcessId, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsRegisteredTestProcess);
        Assert.NotEmpty(snapshot.LogicalProcessorIndices);
    }

    [Fact]
    public async Task PowerPlanPortUsesOnlyFixedPowerCfgCommandsAndGuid()
    {
        var active = Guid.NewGuid();
        var requested = Guid.NewGuid();
        var runner = new RecordingCommandRunner(
            new WindowsCommandResult(
                0,
                $"Power Scheme GUID: {active:D}  (Balanced)",
                string.Empty));
        var port = new WindowsTemporaryPowerPlanPort(runner, @"C:\Windows");

        var snapshot = await port.CaptureActiveAsync(CancellationToken.None);
        await port.ActivateAsync(requested, CancellationToken.None);
        await port.RestoreAsync(snapshot, CancellationToken.None);

        Assert.Equal(active, snapshot.PowerPlanId);
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["/getactivescheme"], command.Arguments),
            command => Assert.Equal(
                ["/setactive", requested.ToString("D")],
                command.Arguments),
            command => Assert.Equal(
                ["/setactive", active.ToString("D")],
                command.Arguments));
        Assert.All(
            runner.Commands,
            command => Assert.EndsWith(
                @"System32\powercfg.exe",
                command.ExecutablePath,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VolumePortUsesBoundIdentityAndFixedMaintenanceMethods()
    {
        var systemId = SystemId.New();
        var volumeId = new StorageObjectId(
            systemId,
            StorageObjectKind.Partition,
            "approved-volume");
        var snapshot = new VolumeTargetSnapshot(
            volumeId,
            @"\\?\VOLUME{11111111-1111-1111-1111-111111111111}",
            "T:\\");
        var resolver = new StubVolumeResolver(snapshot);
        var flush = new RecordingFlushApi();
        var runner = new RecordingCommandRunner(
            new WindowsCommandResult(0, "trim complete", string.Empty));
        var port = new WindowsVolumeMaintenancePort(
            resolver,
            flush,
            runner,
            @"C:\Windows");

        Assert.Same(
            snapshot,
            await port.ResolvePlannedTargetAsync(volumeId, "plan", CancellationToken.None));
        Assert.Same(
            snapshot,
            await port.ResolveCurrentTargetAsync(volumeId, CancellationToken.None));
        var flushEvidence = await port.FlushAsync(snapshot, CancellationToken.None);
        var optimizeEvidence = await port.TrimOrOptimizeAsync(snapshot, CancellationToken.None);

        Assert.Equal(snapshot.StableIdentity, flush.VolumeGuidPath);
        Assert.Equal("Win32.FlushFileBuffers", flushEvidence.Method);
        Assert.Equal("Windows.Defrag.Trim", optimizeEvidence.Method);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(["T:\\", "/L", "/U", "/V"], command.Arguments);
        Assert.EndsWith(
            @"System32\defrag.exe",
            command.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RamMapPortRunsOnlyFixedModeInsideElevatedBroker()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RAMMap64.exe"));
        var identity = new RamMapToolIdentity(
            "binding",
            "1.63",
            "Microsoft Corporation",
            new string('A', 64),
            true);
        var runner = new RecordingCommandRunner(
            new WindowsCommandResult(0, string.Empty, string.Empty));
        var blocked = new DirectElevatedRamMapCacheClearPort(
            path,
            new StubRamMapIdentityProbe(identity),
            runner,
            isInsideElevatedBroker: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => blocked.ClearAsync(
                new RamMapCacheClearRequest(
                    RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                    "plan",
                    RequiresElevatedBroker: true),
                CancellationToken.None));

        var allowed = new DirectElevatedRamMapCacheClearPort(
            path,
            new StubRamMapIdentityProbe(identity),
            runner,
            isInsideElevatedBroker: true);
        Assert.Equal(identity, await allowed.DetectIdentityAsync(CancellationToken.None));
        var evidence = await allowed.ClearAsync(
            new RamMapCacheClearRequest(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                "plan",
                RequiresElevatedBroker: false),
            CancellationToken.None);

        Assert.Equal(["-Es", "-Et"], Assert.Single(runner.Commands).Arguments);
        Assert.True(evidence.UsedElevatedBroker);
    }

    private sealed class RecordingCommandRunner(WindowsCommandResult result)
        : IWindowsCommandRunner
    {
        public List<RecordedCommand> Commands { get; } = [];

        public Task<WindowsCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(new RecordedCommand(executablePath, arguments.ToArray()));
            return Task.FromResult(result);
        }
    }

    private sealed record RecordedCommand(
        string ExecutablePath,
        IReadOnlyList<string> Arguments);

    private sealed class StubVolumeResolver(VolumeTargetSnapshot snapshot)
        : IWindowsVolumeTargetBindingResolver
    {
        public Task<VolumeTargetSnapshot?> ResolvePlannedAsync(
            StorageObjectId volumeId,
            string planHash,
            CancellationToken cancellationToken) =>
            Task.FromResult<VolumeTargetSnapshot?>(snapshot);

        public Task<VolumeTargetSnapshot?> ResolveCurrentAsync(
            StorageObjectId volumeId,
            CancellationToken cancellationToken) =>
            Task.FromResult<VolumeTargetSnapshot?>(snapshot);
    }

    private sealed class RecordingFlushApi : IWindowsVolumeFlushApi
    {
        public string? VolumeGuidPath { get; private set; }

        public void Flush(string volumeGuidPath)
        {
            VolumeGuidPath = volumeGuidPath;
        }
    }

    private sealed class StubRamMapIdentityProbe(RamMapToolIdentity identity)
        : IRamMapExecutableIdentityProbe
    {
        public Task<RamMapToolIdentity?> ProbeAsync(
            string executablePath,
            CancellationToken cancellationToken) =>
            Task.FromResult<RamMapToolIdentity?>(identity);
    }

    private sealed class ToggleProtectionDetector : IWindowsResourceProtectionDetector
    {
        public bool IsProtectedNow { get; set; }

        public bool IsProtected(string fullPath) => IsProtectedNow;
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Infrastructure.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public TemporaryCleanupRoots CreateRoots()
        {
            var winPool = CreateDirectory("winpool");
            var user = CreateDirectory("user");
            var windowsTemp = CreateDirectory("windows", "Temp");
            var windows = Path.Combine(Root, "windows");
            return new TemporaryCleanupRoots(winPool, user, windowsTemp, windows, []);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string CreateDirectory(params string[] parts)
        {
            var path = parts.Aggregate(Root, Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
