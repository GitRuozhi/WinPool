using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.ToolManagement;

public sealed record PreparedPortableToolInstall(
    ToolInstallPlan FinalizedPlan,
    string PackageSha256,
    string SelectedArchiveEntry,
    string ExecutableFileName);

public interface IToolPackageDownloader
{
    Task DownloadAsync(
        Uri source,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class HttpToolPackageDownloader(HttpClient httpClient)
    : IToolPackageDownloader
{
    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (source.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Tool packages must use an HTTPS official source.");
        }

        using var response = await _httpClient
            .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException(
                "The official tool download redirected outside HTTPS.");
        }
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
        {
            throw new InvalidDataException("The tool package exceeds the configured size limit.");
        }

        await using var input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The tool package exceeds the configured size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public interface IToolExecutableTrustVerifier
{
    Task<bool> IsTrustedAsync(string executablePath, CancellationToken cancellationToken);
}

public sealed class ControlledPortableToolInstaller : IToolInstaller
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly ToolCatalog _catalog;
    private readonly IToolPackageDownloader _downloader;
    private readonly IToolExecutableTrustVerifier _trustVerifier;
    private readonly IMutableToolPathConfiguration _pathConfiguration;
    private readonly string _stagingRoot;
    private readonly string _managedRoot;
    private readonly TimeProvider _timeProvider;

    public ControlledPortableToolInstaller(
        ToolCatalog catalog,
        IToolPackageDownloader downloader,
        IToolExecutableTrustVerifier trustVerifier,
        IMutableToolPathConfiguration pathConfiguration,
        string stagingRoot,
        string managedRoot,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _trustVerifier = trustVerifier ?? throw new ArgumentNullException(nameof(trustVerifier));
        _pathConfiguration =
            pathConfiguration ?? throw new ArgumentNullException(nameof(pathConfiguration));
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ApplicationResult<ToolInstallPlan>> PlanAsync(
        ToolId toolId,
        ToolInstallLocation location,
        CancellationToken cancellationToken) =>
        new PlanningOnlyToolInstaller(_catalog, _timeProvider)
            .PlanAsync(toolId, location, cancellationToken);

    public async Task<ApplicationResult<PreparedPortableToolInstall>> PrepareAsync(
        ToolInstallPlan initialPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialPlan);
        var correlationId = CorrelationId.New();
        if (!_catalog.TryGet(initialPlan.ToolId, out var descriptor) ||
            descriptor.InstallerKind != ToolInstallerKind.PortableArchive ||
            initialPlan.InstallerKind != ToolInstallerKind.PortableArchive ||
            initialPlan.Location != ToolInstallLocation.PerUserManagedDirectory ||
            initialPlan.OfficialSource != descriptor.OfficialInstallSource ||
            initialPlan.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return Reject<PreparedPortableToolInstall>(
                correlationId,
                "tool.install.prepare-plan-invalid",
                "The portable installation plan does not match the registered catalog.");
        }

        Directory.CreateDirectory(_stagingRoot);
        var temporaryPath = Path.Combine(
            _stagingRoot,
            $"{descriptor.Id.Value}.{Guid.NewGuid():N}.download");
        try
        {
            await _downloader.DownloadAsync(
                descriptor.OfficialInstallSource,
                temporaryPath,
                MaximumPackageBytes,
                cancellationToken).ConfigureAwait(false);
            var packageSha = await ComputeSha256Async(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            var selected = SelectExecutable(temporaryPath, descriptor);
            var packagePath = PackagePath(packageSha);
            if (!File.Exists(packagePath))
            {
                File.Move(temporaryPath, packagePath);
            }
            else
            {
                File.Delete(temporaryPath);
            }

            var createdAt = _timeProvider.GetUtcNow();
            var expiresAt = createdAt.AddMinutes(15);
            var finalized = initialPlan with
            {
                ExpectedSha256 = packageSha,
                CreatedAtUtc = createdAt,
                ExpiresAtUtc = expiresAt,
                PlanHash = ToolInstallPlanHasher.Compute(
                    descriptor,
                    initialPlan.Location,
                    packageSha,
                    createdAt,
                    expiresAt)
            };
            return ApplicationResult<PreparedPortableToolInstall>.Succeeded(
                new PreparedPortableToolInstall(
                    finalized,
                    packageSha,
                    selected.FullName,
                    Path.GetFileName(selected.FullName)),
                correlationId);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Reject<PreparedPortableToolInstall>(
                correlationId,
                "tool.install.download-timeout",
                exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            return Reject<PreparedPortableToolInstall>(
                correlationId,
                "tool.install.prepare-failed",
                exception.Message);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ApplicationResult<ToolInstallResult>> InstallAsync(
        AuthorizedToolInstall install,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(install);
        var correlationId = CorrelationId.New();
        var plan = install.Plan;
        if (!_catalog.TryGet(plan.ToolId, out var descriptor) ||
            descriptor.InstallerKind != ToolInstallerKind.PortableArchive ||
            plan.InstallerKind != ToolInstallerKind.PortableArchive ||
            plan.Location != ToolInstallLocation.PerUserManagedDirectory ||
            plan.OfficialSource != descriptor.OfficialInstallSource ||
            plan.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            plan.ExpectedSha256.Length != 64 ||
            !StringComparer.Ordinal.Equals(
                plan.PlanHash,
                ToolInstallPlanHasher.Compute(
                    descriptor,
                    plan.Location,
                    plan.ExpectedSha256,
                    plan.CreatedAtUtc,
                    plan.ExpiresAtUtc)))
        {
            return Reject<ToolInstallResult>(
                correlationId,
                "tool.install.authorization-invalid",
                "The authorized portable install no longer matches the catalog and plan hash.");
        }

        var packagePath = PackagePath(plan.ExpectedSha256);
        if (!File.Exists(packagePath) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false),
                plan.ExpectedSha256))
        {
            return Reject<ToolInstallResult>(
                correlationId,
                "tool.install.package-identity-changed",
                "The staged package is missing or its SHA-256 changed.");
        }

        try
        {
            var selected = SelectExecutable(packagePath, descriptor);
            using var archive = ZipFile.OpenRead(packagePath);
            var selectedEntry = archive.GetEntry(selected.FullName)
                ?? throw new InvalidDataException(
                    "The selected executable disappeared from the staged archive.");
            var destinationDirectory = Path.Combine(
                _managedRoot,
                SanitizeDirectoryName(descriptor.Id.Value),
                plan.ExpectedSha256.ToLowerInvariant());
            Directory.CreateDirectory(destinationDirectory);
            var destinationPath = Path.Combine(
                destinationDirectory,
                Path.GetFileName(selected.FullName));
            var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var input = selectedEntry.Open())
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!await _trustVerifier
                        .IsTrustedAsync(temporaryPath, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return Reject<ToolInstallResult>(
                        correlationId,
                        "tool.install.executable-signature-invalid",
                        "The selected executable has no trusted Authenticode signature.");
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            await _pathConfiguration.SetCustomExecutablePathAsync(
                plan.ToolId,
                destinationPath,
                cancellationToken).ConfigureAwait(false);
            var version = FileVersionInfo.GetVersionInfo(destinationPath).ProductVersion;
            var state = new ToolState(
                plan.ToolId,
                ToolAvailability.Available,
                destinationPath,
                ToolPathSource.ManagedInstallation,
                version,
                await ComputeSha256Async(destinationPath, cancellationToken)
                    .ConfigureAwait(false),
                null,
                descriptor.Capabilities,
                descriptor.RequiresElevationForUse);
            return ApplicationResult<ToolInstallResult>.Succeeded(
                new ToolInstallResult(plan.ToolId, state, _timeProvider.GetUtcNow()),
                correlationId);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Reject<ToolInstallResult>(
                correlationId,
                "tool.install.execution-failed",
                exception.Message);
        }
    }

    private string PackagePath(string sha256) =>
        Path.Combine(_stagingRoot, $"{sha256.ToLowerInvariant()}.zip");

    private static SelectedArchiveEntry SelectExecutable(
        string packagePath,
        ToolDescriptor descriptor)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var selected = archive.Entries
            .Where(entry => descriptor.ExecutableFileNames.Contains(
                Path.GetFileName(entry.FullName),
                StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(entry => Score(entry.FullName, descriptor.Id))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selected is null || selected.Length <= 0)
        {
            throw new InvalidDataException(
                "The official archive contains no registered executable.");
        }

        return new SelectedArchiveEntry(selected.FullName, selected.Length);
    }

    private static int Score(string entryName, ToolId toolId)
    {
        var normalized = entryName.Replace('\\', '/');
        if (toolId == KnownToolIds.RamMap)
        {
            return Path.GetFileName(normalized).Equals(
                "RAMMap64.exe",
                StringComparison.OrdinalIgnoreCase)
                ? 100
                : 10;
        }

        return normalized.Contains("/amd64/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/x64/", StringComparison.OrdinalIgnoreCase)
            ? 100
            : 10;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string SanitizeDirectoryName(string value) =>
        string.Concat(
            value.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static ApplicationResult<T> Reject<T>(
        CorrelationId correlationId,
        string code,
        string diagnostic) =>
        ApplicationResult<T>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(
                code,
                "ToolManagement.InstallFailed",
                diagnostic,
                ApplicationMessageSeverity.Error,
                Array.Empty<StorageObjectId>()));

    private sealed record SelectedArchiveEntry(string FullName, long Length);
}
