using WinPool.Application;

namespace WinPool.ToolManagement;

public sealed class ToolPathConfigurationCoordinator
{
    private readonly ToolCatalog catalog;
    private readonly IMutableToolPathConfiguration configuration;
    private readonly IExternalToolRegistry registry;

    public ToolPathConfigurationCoordinator(
        ToolCatalog catalog,
        IMutableToolPathConfiguration configuration,
        IExternalToolRegistry registry)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<ApplicationResult<ToolState>> ConfigureAsync(
        ToolId toolId,
        string? executablePath,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (!catalog.TryGet(toolId, out var descriptor))
        {
            return Rejected(correlationId, "agent.tool.unknown_tool");
        }

        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            if (!Path.IsPathFullyQualified(executablePath))
            {
                return Rejected(correlationId, "agent.tool.path_not_absolute");
            }

            normalized = Path.GetFullPath(executablePath);
            if (!descriptor.ExecutableFileNames.Contains(
                    Path.GetFileName(normalized),
                    StringComparer.OrdinalIgnoreCase))
            {
                return Rejected(
                    correlationId,
                    "agent.tool.executable_name_mismatch");
            }

            if (!File.Exists(normalized))
            {
                return Rejected(
                    correlationId,
                    "agent.tool.executable_not_found");
            }
        }

        await configuration.SetCustomExecutablePathAsync(
            toolId,
            normalized,
            cancellationToken);
        var detected = await registry.DetectAsync(toolId, cancellationToken);
        return new(
            detected.Status,
            detected.Value,
            detected.Messages,
            correlationId);
    }

    private static ApplicationResult<ToolState> Rejected(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<ToolState>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(
                code,
                code,
                string.Empty,
                ApplicationMessageSeverity.Warning,
                []));
}
