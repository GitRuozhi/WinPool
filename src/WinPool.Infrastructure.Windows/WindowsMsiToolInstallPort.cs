using System.Security.Cryptography;
using WinPool.Application;
using WinPool.ToolManagement;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsMsiToolInstallPort : IMsiToolInstallPort
{
    private readonly string _dataRoot;
    private readonly string _windowsDirectory;
    private readonly ToolCatalog _catalog;
    private readonly IWindowsCommandRunner _runner;

    public WindowsMsiToolInstallPort(
        string dataRoot,
        ToolCatalog catalog,
        IWindowsCommandRunner runner,
        string? windowsDirectory = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _windowsDirectory = Path.GetFullPath(
            windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    public async Task<MsiToolInstallEvidence> InstallAsync(
        MsiToolInstallSnapshot package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!_catalog.TryGet(package.ToolId, out var descriptor) ||
            descriptor.InstallerKind != ToolInstallerKind.Msi ||
            !descriptor.RequiresElevationForInstall ||
            string.IsNullOrWhiteSpace(descriptor.OfficialPackageSha256) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                descriptor.OfficialPackageSha256,
                package.Sha256))
        {
            throw new InvalidOperationException("The MSI is not pinned in the WinPool tool catalog.");
        }

        var expectedRelative = Path.Combine(
            "tool-downloads",
            $"{package.Sha256.ToLowerInvariant()}.msi");
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                expectedRelative,
                package.PackageRelativePath))
        {
            throw new InvalidDataException("The MSI staging path is not the fixed catalog path.");
        }

        var packagePath = Path.GetFullPath(Path.Combine(_dataRoot, package.PackageRelativePath));
        var stagingRoot = Path.GetFullPath(Path.Combine(_dataRoot, "tool-downloads"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!packagePath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(packagePath) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false),
                package.Sha256))
        {
            throw new InvalidDataException("The staged MSI is missing or its SHA-256 changed.");
        }

        var msiexec = Path.Combine(_windowsDirectory, "System32", "msiexec.exe");
        var result = await _runner.RunAsync(
            msiexec,
            ["/i", packagePath, "/passive", "/norestart"],
            cancellationToken).ConfigureAwait(false);
        return new MsiToolInstallEvidence(
            result.ExitCode,
            result.ExitCode is 1641 or 3010);
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
}
