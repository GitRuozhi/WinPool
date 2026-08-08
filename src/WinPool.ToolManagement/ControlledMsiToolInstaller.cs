using System.Security.Cryptography;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.ToolManagement;

public sealed record PreparedMsiToolInstall(
    ToolInstallPlan FinalizedPlan,
    string PackageSha256,
    string PackageRelativePath);

/// <summary>
/// Downloads and stages a catalog-pinned MSI. It never starts MSIEXEC and never
/// elevates; execution belongs to the one-shot elevated Broker.
/// </summary>
public sealed class ControlledMsiToolInstaller
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private readonly ToolCatalog _catalog;
    private readonly IToolPackageDownloader _downloader;
    private readonly string _dataRoot;
    private readonly TimeProvider _timeProvider;

    public ControlledMsiToolInstaller(
        ToolCatalog catalog,
        IToolPackageDownloader downloader,
        string dataRoot,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _dataRoot = Path.GetFullPath(dataRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApplicationResult<PreparedMsiToolInstall>> PrepareAsync(
        ToolInstallPlan initialPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialPlan);
        var correlationId = CorrelationId.New();
        if (!_catalog.TryGet(initialPlan.ToolId, out var descriptor) ||
            descriptor.InstallerKind != ToolInstallerKind.Msi ||
            initialPlan.InstallerKind != ToolInstallerKind.Msi ||
            initialPlan.OfficialSource != descriptor.OfficialInstallSource ||
            initialPlan.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            string.IsNullOrWhiteSpace(descriptor.OfficialPackageSha256))
        {
            return Reject(correlationId, "tool.install.msi-plan-invalid");
        }

        var expected = descriptor.OfficialPackageSha256.ToUpperInvariant();
        var stagingRoot = Path.Combine(_dataRoot, "tool-downloads");
        Directory.CreateDirectory(stagingRoot);
        var temporaryPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.download");
        var packagePath = Path.Combine(stagingRoot, $"{expected.ToLowerInvariant()}.msi");
        try
        {
            await _downloader.DownloadAsync(
                descriptor.OfficialInstallSource,
                temporaryPath,
                MaximumPackageBytes,
                cancellationToken).ConfigureAwait(false);
            var actual = await ComputeSha256Async(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(actual, expected))
            {
                return Reject(correlationId, "tool.install.msi-hash-mismatch");
            }

            File.Move(temporaryPath, packagePath, overwrite: true);

            var createdAt = _timeProvider.GetUtcNow();
            var expiresAt = createdAt.AddMinutes(15);
            var finalized = initialPlan with
            {
                ExpectedSha256 = expected,
                CreatedAtUtc = createdAt,
                ExpiresAtUtc = expiresAt,
                PlanHash = ToolInstallPlanHasher.Compute(
                    descriptor,
                    initialPlan.Location,
                    expected,
                    createdAt,
                    expiresAt)
            };
            return ApplicationResult<PreparedMsiToolInstall>.Succeeded(
                new PreparedMsiToolInstall(
                    finalized,
                    expected,
                    Path.Combine("tool-downloads", $"{expected.ToLowerInvariant()}.msi")),
                correlationId);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            return Reject(correlationId, "tool.install.msi-prepare-failed", exception.Message);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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

    private static ApplicationResult<PreparedMsiToolInstall> Reject(
        CorrelationId correlationId,
        string code,
        string? diagnostic = null) =>
        ApplicationResult<PreparedMsiToolInstall>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(
                code,
                "ToolManagement.InstallFailed",
                diagnostic ?? code,
                ApplicationMessageSeverity.Error,
                []));
}
