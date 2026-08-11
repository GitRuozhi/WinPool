using System.Reflection;
using System.Security.Cryptography;
using System.IO.Compression;
using WinPool.Application;

namespace WinPool.ToolManagement.Tests;

public sealed class ToolManagementTests
{
    [Fact]
    public void Catalog_RegistersRequiredExternalTools_WithHttpsOfficialSources()
    {
        var descriptors = new ToolCatalog().List();

        Assert.Equal(
            [
                KnownToolIds.DiskSpd,
                KnownToolIds.Fio,
                KnownToolIds.DiteFileGen,
                KnownToolIds.RoboCopy,
                KnownToolIds.RamMap
            ],
            descriptors.Select(descriptor => descriptor.Id));
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(Uri.UriSchemeHttps, descriptor.OfficialHomePage.Scheme);
            Assert.Equal(Uri.UriSchemeHttps, descriptor.OfficialInstallSource.Scheme);
            Assert.NotEmpty(descriptor.ExecutableFileNames);
            Assert.DoesNotContain(descriptor.ExecutableFileNames, string.IsNullOrWhiteSpace);
        });
        Assert.Contains(
            descriptors,
            descriptor => descriptor.Id == KnownToolIds.RamMap
                && descriptor.RequiresElevationForUse
                && !descriptor.RequiresElevationForInstall
                && descriptor.Capabilities == ToolCapabilities.SystemCacheCleanup);
        Assert.Contains(
            descriptors,
            descriptor => descriptor.Id == KnownToolIds.RoboCopy
                && descriptor.InstallerKind is null);
        Assert.Contains(
            descriptors,
            descriptor => descriptor.Id == KnownToolIds.DiteFileGen
                && descriptor.AllowMissingVersionMetadata
                && descriptor.InstallerKind is null);
    }

    [Theory]
    [InlineData("DISKSPD 2.2", 2, 2, 0, 0)]
    [InlineData("fio-3.42-64-generic", 3, 42, 0, 0)]
    [InlineData("10.0.26100.1882", 10, 0, 26100, 1882)]
    [InlineData("RAMMap v1.63", 1, 63, 0, 0)]
    public void VersionParser_ExtractsRegisteredToolVersionShapes(
        string text,
        int major,
        int minor,
        int build,
        int revision)
    {
        Assert.True(ToolVersionParser.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build < 0 ? 0 : version.Build);
        Assert.Equal(revision, version.Revision < 0 ? 0 : version.Revision);
    }

    [Fact]
    public async Task Registry_CustomPathTakesPrecedence_AndReturnsVersionAndSha256()
    {
        using var workspace = new TemporaryWorkspace();
        var customFile = workspace.Write("custom/diskspd.exe", [1, 2, 3, 4]);
        workspace.Write("path/diskspd.exe", [9, 9, 9]);
        var registry = CreateRegistry(
            new Dictionary<ToolId, string> { [KnownToolIds.DiskSpd] = customFile },
            [workspace.Directory("path")],
            new StubVersionProbe("DISKSPD 2.2", "Microsoft Corporation"));

        var result = await registry.DetectDetailedAsync(KnownToolIds.DiskSpd, CancellationToken.None);

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.Available, result.Value.State.Availability);
        Assert.Equal(ToolPathSource.CustomPath, result.Value.State.PathSource);
        Assert.Equal(Path.GetFullPath(customFile), result.Value.State.ExecutablePath);
        Assert.Equal("DISKSPD 2.2", result.Value.State.Version);
        Assert.Equal("Microsoft Corporation", result.Value.State.Publisher);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])), result.Value.State.Sha256);
        Assert.Equal(ToolVersionSupportStatus.Supported, result.Value.VersionSupport);
    }

    [Fact]
    public async Task Registry_InvalidCustomPath_DoesNotSilentlyFallbackToPath()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("path/fio.exe", [1]);
        var registry = CreateRegistry(
            new Dictionary<ToolId, string>
            {
                [KnownToolIds.Fio] = Path.Combine(workspace.Root, "missing", "fio.exe")
            },
            [workspace.Directory("path")],
            new StubVersionProbe("fio-3.42"));

        var result = await registry.DetectDetailedAsync(KnownToolIds.Fio, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.Misconfigured, result.Value.State.Availability);
        Assert.Equal(ToolPathSource.CustomPath, result.Value.State.PathSource);
        Assert.Equal("tool.custom-path.invalid", result.Value.DiagnosticCode);
    }

    [Fact]
    public async Task Registry_RelativeCustomPath_IsRejected()
    {
        var registry = CreateRegistry(
            new Dictionary<ToolId, string> { [KnownToolIds.Fio] = "fio.exe" },
            [],
            new StubVersionProbe("fio-3.42"));

        var result = await registry.DetectDetailedAsync(KnownToolIds.Fio, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.Misconfigured, result.Value.State.Availability);
        Assert.Equal("tool.custom-path.invalid", result.Value.DiagnosticCode);
    }

    [Fact]
    public async Task Registry_PathDiscovery_OnlyUsesRegisteredExecutableNames()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("path/not-fio.exe", [1]);
        var expected = workspace.Write("path/fio.exe", [2]);
        var registry = CreateRegistry(
            null,
            [workspace.Directory("path")],
            new StubVersionProbe("fio-3.42"));

        var result = await registry.DetectDetailedAsync(KnownToolIds.Fio, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.Available, result.Value.State.Availability);
        Assert.Equal(ToolPathSource.AutomaticDiscovery, result.Value.State.PathSource);
        Assert.Equal(Path.GetFullPath(expected), result.Value.State.ExecutablePath);
    }

    [Fact]
    public async Task Registry_MissingTool_IsNotFound_WithoutCallingVersionProbe()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new StubVersionProbe("2.2");
        var registry = CreateRegistry(null, [workspace.Root], probe);

        var result = await registry.DetectDetailedAsync(KnownToolIds.DiskSpd, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.NotFound, result.Value.State.Availability);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Registry_UnsupportedVersion_IsExplicit()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("path/diskspd.exe", [1]);
        var registry = CreateRegistry(
            null,
            [workspace.Directory("path")],
            new StubVersionProbe("DISKSPD 3.0"));

        var result = await registry.DetectDetailedAsync(KnownToolIds.DiskSpd, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.UnsupportedVersion, result.Value.State.Availability);
        Assert.Equal(ToolVersionSupportStatus.Unsupported, result.Value.VersionSupport);
        Assert.Equal("tool.version.unsupported", result.Value.DiagnosticCode);
    }

    [Fact]
    public async Task Registry_ProbeFailure_IsExplicitlyMisconfigured()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("path/diskspd.exe", [1]);
        var registry = CreateRegistry(
            null,
            [workspace.Directory("path")],
            new FailedVersionProbe());

        var result = await registry.DetectDetailedAsync(KnownToolIds.DiskSpd, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.Misconfigured, result.Value.State.Availability);
        Assert.Equal(ToolVersionSupportStatus.ProbeFailed, result.Value.VersionSupport);
        Assert.Equal("tool.version.test-failure", result.Value.DiagnosticCode);
        Assert.NotNull(result.Value.State.Sha256);
    }

    [Fact]
    public async Task Registry_ChangedHash_IsIdentityChanged()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("path/RAMMap.exe", [1, 2, 3]);
        var baseline = new ToolIdentityBaseline(
            new Dictionary<ToolId, string> { [KnownToolIds.RamMap] = new string('0', 64) });
        var registry = CreateRegistry(
            null,
            [workspace.Directory("path")],
            new StubVersionProbe("RAMMap v1.63", "Microsoft Corporation"),
            baseline);

        var result = await registry.DetectDetailedAsync(KnownToolIds.RamMap, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(ToolAvailability.IdentityChanged, result.Value.State.Availability);
        Assert.Equal("tool.identity.changed", result.Value.DiagnosticCode);
        Assert.True(result.Value.State.RequiresElevation);
    }

    [Fact]
    public async Task Registry_RejectsUnregisteredToolId()
    {
        var registry = CreateRegistry(null, [], new StubVersionProbe("1.0"));

        var result = await registry.DetectAsync(new ToolId("user.command"), CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(result.Messages, message => message.Code == "tool.unknown");
    }

    [Fact]
    public async Task Registry_ListReturnsAllRegisteredTools_InCatalogOrder()
    {
        using var workspace = new TemporaryWorkspace();
        var registry = CreateRegistry(null, [workspace.Root], new StubVersionProbe("1.0"));

        var result = await registry.ListAsync(CancellationToken.None);

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(
            [
                KnownToolIds.DiskSpd,
                KnownToolIds.Fio,
                KnownToolIds.DiteFileGen,
                KnownToolIds.RoboCopy,
                KnownToolIds.RamMap
            ],
            result.Value.Select(state => state.ToolId));
        Assert.All(result.Value, state => Assert.Equal(ToolAvailability.NotFound, state.Availability));
    }

    [Fact]
    public async Task DiteFileGenMayUseHashBoundCustomExecutableWithoutVersionResource()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = workspace.Write("Dite.exe", [1, 2, 3]);
        var registry = CreateRegistry(
            new Dictionary<ToolId, string>
            {
                [KnownToolIds.DiteFileGen] = executable
            },
            [],
            new FailedVersionProbe());

        var result = await registry.DetectDetailedAsync(
            KnownToolIds.DiteFileGen,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ToolAvailability.Available, result.Value!.State.Availability);
        Assert.Equal(
            ToolVersionSupportStatus.Unrecognized,
            result.Value.VersionSupport);
        Assert.Matches("^[0-9A-F]{64}$", result.Value.State.Sha256);
        Assert.Equal(
            "tool.available.version-metadata-missing",
            result.Value.DiagnosticCode);
    }

    [Fact]
    public async Task JsonConfiguration_IsAtomicallyVisibleAcrossInstances_AndCanBeCleared()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "settings", "tool-paths.json");
        var writer = new JsonToolPathConfiguration(path);
        var reader = new JsonToolPathConfiguration(path);
        var executable = workspace.Write("tools/diskspd.exe", [1]);

        await writer.SetCustomExecutablePathAsync(
            KnownToolIds.DiskSpd,
            executable,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(executable), reader.GetCustomExecutablePath(KnownToolIds.DiskSpd));
        Assert.Null(reader.GetCustomExecutablePath(KnownToolIds.Fio));
        Assert.Empty(
            System.IO.Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                "*.tmp"));

        await writer.SetCustomExecutablePathAsync(
            KnownToolIds.DiskSpd,
            null,
            CancellationToken.None);

        Assert.Null(reader.GetCustomExecutablePath(KnownToolIds.DiskSpd));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task JsonConfiguration_RejectsRelativeCustomPath()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new JsonToolPathConfiguration(
            Path.Combine(workspace.Root, "tool-paths.json"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => configuration.SetCustomExecutablePathAsync(
                KnownToolIds.Fio,
                "fio.exe",
                CancellationToken.None));
    }

    [Fact]
    public async Task ConfigurationCoordinatorValidatesWritesAndRedetectsActivePath()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new JsonToolPathConfiguration(
            Path.Combine(workspace.Root, "active", "tool-paths.json"));
        var catalog = new ToolCatalog();
        var registry = new ExternalToolRegistry(
            catalog,
            new ToolPathDiscovery(configuration, new FixedSearchPath([])),
            new StubVersionProbe("3.42"),
            new Sha256ToolFileHasher());
        var coordinator = new ToolPathConfigurationCoordinator(
            catalog,
            configuration,
            registry);
        var executable = workspace.Write("tools/fio.exe", [1, 2, 3]);

        var configured = await coordinator.ConfigureAsync(
            KnownToolIds.Fio,
            executable,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.True(configured.IsSuccess);
        Assert.Equal(Path.GetFullPath(executable), configured.Value!.ExecutablePath);
        Assert.Equal(ToolPathSource.CustomPath, configured.Value.PathSource);
        Assert.Equal(
            Path.GetFullPath(executable),
            configuration.GetCustomExecutablePath(KnownToolIds.Fio));

        var cleared = await coordinator.ConfigureAsync(
            KnownToolIds.Fio,
            null,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Null(configuration.GetCustomExecutablePath(KnownToolIds.Fio));
        Assert.Equal(ToolAvailability.NotFound, cleared.Value!.Availability);
    }

    [Fact]
    public async Task ConfigurationCoordinatorRejectsWrongExecutableWithoutWriting()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new JsonToolPathConfiguration(
            Path.Combine(workspace.Root, "active", "tool-paths.json"));
        var catalog = new ToolCatalog();
        var registry = new ExternalToolRegistry(
            catalog,
            new ToolPathDiscovery(configuration, new FixedSearchPath([])),
            new StubVersionProbe("1"),
            new Sha256ToolFileHasher());
        var coordinator = new ToolPathConfigurationCoordinator(
            catalog,
            configuration,
            registry);
        var wrongName = workspace.Write("tools/not-fio.exe", [1]);

        var result = await coordinator.ConfigureAsync(
            KnownToolIds.Fio,
            wrongName,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "agent.tool.executable_name_mismatch");
        Assert.Null(configuration.GetCustomExecutablePath(KnownToolIds.Fio));
    }

    [Fact]
    public async Task InstallPlanner_OnlyCreatesOfficialPlan_AndRequiresConfirmation()
    {
        var now = new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero);
        var planner = new PlanningOnlyToolInstaller(
            new ToolCatalog(),
            new FixedTimeProvider(now));

        var result = await planner.PlanAsync(
            KnownToolIds.DiskSpd,
            ToolInstallLocation.PerUserManagedDirectory,
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.RequiresAuthorization, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(
            new Uri("https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.zip"),
            result.Value.OfficialSource);
        Assert.Equal(now, result.Value.CreatedAtUtc);
        Assert.Equal(now.AddMinutes(15), result.Value.ExpiresAtUtc);
        Assert.Matches("^[0-9A-F]{64}$", result.Value.PlanHash);
        Assert.Contains(result.Messages, message => message.Code == "tool.install.confirmation-required");
        Assert.Contains(result.Messages, message => message.Code == "tool.install.official-hash-unavailable");
    }

    [Fact]
    public async Task InstallPlanner_RejectsIndependentInstallForWindowsComponent()
    {
        var planner = new PlanningOnlyToolInstaller(new ToolCatalog());

        var result = await planner.PlanAsync(
            KnownToolIds.RoboCopy,
            ToolInstallLocation.PerUserManagedDirectory,
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(result.Messages, message => message.Code == "tool.install.windows-component");
    }

    [Fact]
    public async Task PlanningOnlyInstaller_CannotInstall_EvenIfCalledDirectly()
    {
        var planner = new PlanningOnlyToolInstaller(new ToolCatalog());

        var result = await planner.InstallAsync(null!, CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(
            result.Messages,
            message => message.Code == "tool.install.execution-not-implemented");
    }

    [Fact]
    public async Task ControlledPortableInstallerDownloadsReviewsRechecksAndConfiguresPath()
    {
        using var workspace = new TemporaryWorkspace();
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var paths = new JsonToolPathConfiguration(
            Path.Combine(workspace.Root, "tool-paths.json"));
        var installer = new ControlledPortableToolInstaller(
            new ToolCatalog(),
            new FakeArchiveDownloader("DiskSpd/amd64/diskspd.exe", [1, 2, 3, 4]),
            new FixedTrustVerifier(true),
            paths,
            workspace.Directory("staging"),
            workspace.Directory("managed"),
            new FixedTimeProvider(now));
        var plan = Assert.IsType<ToolInstallPlan>(
            (await installer.PlanAsync(
                KnownToolIds.DiskSpd,
                ToolInstallLocation.PerUserManagedDirectory,
                CancellationToken.None)).Value);

        var prepared = await installer.PrepareAsync(plan, CancellationToken.None);
        Assert.True(prepared.IsSuccess);
        Assert.NotNull(prepared.Value);
        Assert.Matches("^[0-9A-F]{64}$", prepared.Value.PackageSha256);
        Assert.Equal("DiskSpd/amd64/diskspd.exe", prepared.Value.SelectedArchiveEntry);
        var authorization = ToolInstallAuthorization.Authorize(
            prepared.Value.FinalizedPlan,
            userConfirmed: true,
            now,
            CorrelationId.New());
        Assert.True(authorization.IsSuccess);

        var installed = await installer.InstallAsync(
            authorization.Value!,
            CancellationToken.None);

        Assert.True(installed.IsSuccess);
        Assert.NotNull(installed.Value);
        Assert.Equal(
            ToolPathSource.ManagedInstallation,
            installed.Value.State.PathSource);
        Assert.True(File.Exists(installed.Value.State.ExecutablePath));
        Assert.Equal(
            installed.Value.State.ExecutablePath,
            paths.GetCustomExecutablePath(KnownToolIds.DiskSpd));
    }

    [Fact]
    public async Task ControlledMsiInstallerStagesOnlyCatalogPinnedHash()
    {
        using var workspace = new TemporaryWorkspace();
        byte[] package = [1, 3, 3, 7];
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var descriptor = MsiDescriptor(hash);
        var catalog = new ToolCatalog([descriptor]);
        var now = new DateTimeOffset(2026, 8, 1, 1, 2, 3, TimeSpan.Zero);
        var planner = new PlanningOnlyToolInstaller(catalog, new FixedTimeProvider(now));
        var plan = Assert.IsType<ToolInstallPlan>(
            (await planner.PlanAsync(
                KnownToolIds.Fio,
                ToolInstallLocation.PerUserManagedDirectory,
                CancellationToken.None)).Value);
        var installer = new ControlledMsiToolInstaller(
            catalog,
            new RawPackageDownloader(package),
            workspace.Root,
            new FixedTimeProvider(now));

        var result = await installer.PrepareAsync(plan, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(hash, result.Value.PackageSha256);
        Assert.Equal(
            Path.Combine("tool-downloads", $"{hash.ToLowerInvariant()}.msi"),
            result.Value.PackageRelativePath);
        Assert.True(File.Exists(Path.Combine(workspace.Root, result.Value.PackageRelativePath)));
    }

    [Fact]
    public async Task ControlledMsiInstallerRejectsDownloadedHashDrift()
    {
        using var workspace = new TemporaryWorkspace();
        var catalog = new ToolCatalog([MsiDescriptor(new string('A', 64))]);
        var plan = Assert.IsType<ToolInstallPlan>(
            (await new PlanningOnlyToolInstaller(catalog).PlanAsync(
                KnownToolIds.Fio,
                ToolInstallLocation.PerUserManagedDirectory,
                CancellationToken.None)).Value);
        var installer = new ControlledMsiToolInstaller(
            catalog,
            new RawPackageDownloader([9, 9, 9]),
            workspace.Root);

        var result = await installer.PrepareAsync(plan, CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "tool.install.msi-hash-mismatch");
    }

    [Fact]
    public async Task ToolInstallAuthorizationRequiresConfirmationAndVerifiedHash()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new ToolInstallPlan(
            KnownToolIds.DiskSpd,
            new Uri("https://example.invalid/tool.zip"),
            new string('A', 64),
            ToolInstallerKind.PortableArchive,
            ToolInstallLocation.PerUserManagedDirectory,
            false,
            now,
            now.AddMinutes(15),
            "plan");

        Assert.Equal(
            ApplicationStatus.Rejected,
            ToolInstallAuthorization.Authorize(
                plan,
                userConfirmed: false,
                now,
                CorrelationId.New()).Status);
        Assert.Equal(
            ApplicationStatus.Rejected,
            ToolInstallAuthorization.Authorize(
                plan with { ExpectedSha256 = string.Empty },
                userConfirmed: true,
                now,
                CorrelationId.New()).Status);
    }

    [Fact]
    public void ToolManagementAssembly_ExposesNoFreeCommandOrArgumentContract()
    {
        var assembly = typeof(ToolCatalog).Assembly;
        var forbiddenProperties = assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property =>
                property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Arguments", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var forbiddenParameters = assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(method => method.GetParameters())
            .Where(parameter =>
                parameter.Name?.Contains("command", StringComparison.OrdinalIgnoreCase) == true
                || parameter.Name?.Contains("arguments", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        Assert.Empty(forbiddenProperties);
        Assert.Empty(forbiddenParameters);
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name == "System.Diagnostics.Process");
    }

    private static ExternalToolRegistry CreateRegistry(
        IReadOnlyDictionary<ToolId, string>? customPaths,
        IReadOnlyList<string> pathDirectories,
        IToolVersionProbe probe,
        IToolIdentityBaseline? baseline = null) =>
        new(
            new ToolCatalog(),
            new ToolPathDiscovery(
                new ToolPathConfiguration(customPaths),
                new FixedSearchPath(pathDirectories)),
            probe,
            new Sha256ToolFileHasher(),
            baseline);

    private sealed class FixedSearchPath(IReadOnlyList<string> directories) : IToolSearchPath
    {
        public IReadOnlyList<string> GetDirectories() => directories;
    }

    private sealed class StubVersionProbe(string version, string? publisher = null) : IToolVersionProbe
    {
        public int CallCount { get; private set; }

        public Task<ToolVersionProbeResult> ProbeAsync(
            ToolVersionProbeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(ToolVersionProbeResult.Success(version, publisher));
        }
    }

    private sealed class FailedVersionProbe : IToolVersionProbe
    {
        public Task<ToolVersionProbeResult> ProbeAsync(
            ToolVersionProbeRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(ToolVersionProbeResult.Failure("tool.version.test-failure"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeArchiveDownloader(
        string executableEntry,
        byte[] executableBytes) : IToolPackageDownloader
    {
        public Task DownloadAsync(
            Uri source,
            string destinationPath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry(executableEntry);
            using var stream = entry.Open();
            stream.Write(executableBytes);
            return Task.CompletedTask;
        }
    }

    private sealed class RawPackageDownloader(byte[] bytes) : IToolPackageDownloader
    {
        public Task DownloadAsync(
            Uri source,
            string destinationPath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(destinationPath, bytes);
            return Task.CompletedTask;
        }
    }

    private static ToolDescriptor MsiDescriptor(string sha256) =>
        new(
            KnownToolIds.Fio,
            "fio",
            "test",
            ["fio.exe"],
            new Uri("https://github.com/axboe/fio"),
            new Uri("https://github.com/axboe/fio/releases/download/test/fio-test-x64.msi"),
            ToolInstallerKind.Msi,
            ToolVersionProbeKind.FileVersionMetadata,
            new ToolVersionPolicy(new Version(3, 31), new Version(4, 0)),
            ToolCapabilities.SequentialIo,
            false,
            true,
            sha256);

    private sealed class FixedTrustVerifier(bool trusted) : IToolExecutableTrustVerifier
    {
        public Task<bool> IsTrustedAsync(
            string executablePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(trusted);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "WinPool.ToolManagement.Tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string Write(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Root, true);
        }
    }
}
