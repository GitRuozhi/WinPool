using System.Security.Cryptography;
using System.Text;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.ToolManagement;

/// <summary>
/// Produces reviewable official-source plans only. It performs no network,
/// package-manager, archive, installer, elevation, or process operation.
/// </summary>
public sealed class PlanningOnlyToolInstaller : IToolInstaller
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
    private readonly ToolCatalog catalog;
    private readonly TimeProvider timeProvider;

    public PlanningOnlyToolInstaller(
        ToolCatalog catalog,
        TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ApplicationResult<ToolInstallPlan>> PlanAsync(
        ToolId toolId,
        ToolInstallLocation location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = CorrelationId.New();
        if (!catalog.TryGet(toolId, out var descriptor))
        {
            return Task.FromResult(
                ApplicationResult<ToolInstallPlan>.FromStatus(
                    ApplicationStatus.Rejected,
                    correlationId,
                    Message(
                        "tool.install.unknown",
                        "ToolManagement.UnknownTool",
                        $"Unregistered ToolId: {toolId.Value}",
                        ApplicationMessageSeverity.Error)));
        }

        if (descriptor.InstallerKind is null)
        {
            return Task.FromResult(
                ApplicationResult<ToolInstallPlan>.FromStatus(
                    ApplicationStatus.Rejected,
                    correlationId,
                    Message(
                        "tool.install.windows-component",
                        "ToolManagement.WindowsComponent",
                        $"'{descriptor.Id.Value}' is a Windows component and has no independent install action.",
                        ApplicationMessageSeverity.Warning)));
        }

        var createdAt = timeProvider.GetUtcNow();
        var expiresAt = createdAt.Add(PlanLifetime);
        var expectedHash = descriptor.OfficialPackageSha256 ?? string.Empty;
        var planHash = ToolInstallPlanHasher.Compute(
            descriptor,
            location,
            expectedHash,
            createdAt,
            expiresAt);
        var plan = new ToolInstallPlan(
            descriptor.Id,
            descriptor.OfficialInstallSource,
            expectedHash,
            descriptor.InstallerKind.Value,
            location,
            descriptor.RequiresElevationForInstall,
            createdAt,
            expiresAt,
            planHash);
        var messages = new List<ApplicationMessage>
        {
            Message(
                "tool.install.confirmation-required",
                "ToolManagement.InstallConfirmationRequired",
                "The plan is inert until the user explicitly confirms it.",
                ApplicationMessageSeverity.Warning)
        };
        if (string.IsNullOrEmpty(expectedHash))
        {
            messages.Add(
                Message(
                    "tool.install.official-hash-unavailable",
                    "ToolManagement.OfficialHashUnavailable",
                    "The registered official source does not provide a fixed package SHA-256; the downloaded artifact must be hashed and its signature verified before authorization.",
                    ApplicationMessageSeverity.Warning));
        }

        return Task.FromResult(
            new ApplicationResult<ToolInstallPlan>(
                ApplicationStatus.RequiresAuthorization,
                plan,
                messages,
                correlationId));
    }

    public Task<ApplicationResult<ToolInstallResult>> InstallAsync(
        AuthorizedToolInstall install,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            ApplicationResult<ToolInstallResult>.FromStatus(
                ApplicationStatus.Rejected,
                CorrelationId.New(),
                Message(
                    "tool.install.execution-not-implemented",
                    "ToolManagement.InstallExecutionUnavailable",
                    "This component only creates installation plans and cannot download or install tools.",
                    ApplicationMessageSeverity.Error)));
    }

    private static ApplicationMessage Message(
        string code,
        string textKey,
        string diagnostic,
        ApplicationMessageSeverity severity) =>
        new(code, textKey, diagnostic, severity, Array.Empty<StorageObjectId>());
}

public static class ToolInstallPlanHasher
{
    public static string Compute(
        ToolDescriptor descriptor,
        ToolInstallLocation location,
        string expectedHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        var canonical = string.Join(
            '\n',
            descriptor.Id.Value,
            descriptor.OfficialInstallSource.AbsoluteUri,
            descriptor.InstallerKind!.Value.ToString(),
            location.ToString(),
            descriptor.RequiresElevationForInstall ? "true" : "false",
            string.IsNullOrEmpty(expectedHash) ? "<unavailable>" : expectedHash.ToUpperInvariant(),
            createdAt.ToUniversalTime().ToString("O"),
            expiresAt.ToUniversalTime().ToString("O"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
