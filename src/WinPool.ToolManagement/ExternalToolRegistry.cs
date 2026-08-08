using WinPool.Application;
using WinPool.Domain;

namespace WinPool.ToolManagement;

public sealed record ToolDetectionResult(
    ToolDescriptor Descriptor,
    ToolState State,
    ToolVersionSupportStatus VersionSupport,
    string DiagnosticCode);

public sealed class ExternalToolRegistry : IExternalToolRegistry
{
    private readonly ToolCatalog catalog;
    private readonly ToolPathDiscovery pathDiscovery;
    private readonly IToolVersionProbe versionProbe;
    private readonly IToolFileHasher fileHasher;
    private readonly IToolIdentityBaseline identityBaseline;

    public ExternalToolRegistry(
        ToolCatalog catalog,
        ToolPathDiscovery pathDiscovery,
        IToolVersionProbe versionProbe,
        IToolFileHasher fileHasher,
        IToolIdentityBaseline? identityBaseline = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.pathDiscovery = pathDiscovery ?? throw new ArgumentNullException(nameof(pathDiscovery));
        this.versionProbe = versionProbe ?? throw new ArgumentNullException(nameof(versionProbe));
        this.fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
        this.identityBaseline = identityBaseline ?? new EmptyToolIdentityBaseline();
    }

    public async Task<ApplicationResult<ToolState>> DetectAsync(
        ToolId toolId,
        CancellationToken cancellationToken)
    {
        var result = await DetectDetailedAsync(toolId, cancellationToken);
        return result.Value is null
            ? ApplicationResult<ToolState>.FromStatus(
                result.Status,
                result.CorrelationId,
                result.Messages.ToArray())
            : new ApplicationResult<ToolState>(
                result.Status,
                result.Value.State,
                result.Messages,
                result.CorrelationId);
    }

    public async Task<ApplicationResult<IReadOnlyList<ToolState>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var states = new List<ToolState>(catalog.List().Count);
        var messages = new List<ApplicationMessage>();
        var status = ApplicationStatus.Succeeded;

        foreach (var descriptor in catalog.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DetectDetailedAsync(descriptor.Id, cancellationToken);
            if (result.Value is not null)
            {
                states.Add(result.Value.State);
            }

            messages.AddRange(result.Messages);
            if (result.Status is ApplicationStatus.Failed)
            {
                status = ApplicationStatus.PartiallyCompleted;
            }
        }

        return new ApplicationResult<IReadOnlyList<ToolState>>(
            status,
            states,
            messages,
            CorrelationId.New());
    }

    public async Task<ApplicationResult<ToolDetectionResult>> DetectDetailedAsync(
        ToolId toolId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = CorrelationId.New();
        if (!catalog.TryGet(toolId, out var descriptor))
        {
            return ApplicationResult<ToolDetectionResult>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message(
                    "tool.unknown",
                    "ToolManagement.UnknownTool",
                    $"Unregistered ToolId: {toolId.Value}",
                    ApplicationMessageSeverity.Error));
        }

        var discovered = pathDiscovery.Find(descriptor);
        if (!discovered.Found || discovered.ExecutablePath is null)
        {
            var missingAvailability = discovered.CustomPathWasInvalid
                ? ToolAvailability.Misconfigured
                : ToolAvailability.NotFound;
            var diagnosticCode = discovered.CustomPathWasInvalid
                ? "tool.custom-path.invalid"
                : "tool.not-found";
            var state = NewState(
                descriptor,
                missingAvailability,
                discovered.ExecutablePath,
                discovered.PathSource);
            return ApplicationResult<ToolDetectionResult>.Succeeded(
                new ToolDetectionResult(
                    descriptor,
                    state,
                    ToolVersionSupportStatus.ProbeFailed,
                    diagnosticCode),
                correlationId);
        }

        string sha256;
        try
        {
            sha256 = await fileHasher.ComputeSha256Async(
                discovered.ExecutablePath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var state = NewState(
                descriptor,
                ToolAvailability.Misconfigured,
                discovered.ExecutablePath,
                discovered.PathSource);
            return new ApplicationResult<ToolDetectionResult>(
                ApplicationStatus.Failed,
                new ToolDetectionResult(
                    descriptor,
                    state,
                    ToolVersionSupportStatus.ProbeFailed,
                    "tool.hash.unreadable"),
                [
                    Message(
                        "tool.hash.unreadable",
                        "ToolManagement.HashUnreadable",
                        $"Unable to hash registered tool '{descriptor.Id.Value}'.",
                        ApplicationMessageSeverity.Error)
                ],
                correlationId);
        }

        var probe = await versionProbe.ProbeAsync(
            new ToolVersionProbeRequest(descriptor, discovered.ExecutablePath),
            cancellationToken);
        if (!probe.Succeeded)
        {
            if (descriptor.AllowMissingVersionMetadata)
            {
                var expectedHashWithoutVersion = identityBaseline.GetExpectedSha256(
                    descriptor.Id,
                    discovered.ExecutablePath);
                var identityChangedWithoutVersion =
                    !string.IsNullOrWhiteSpace(expectedHashWithoutVersion)
                    && !string.Equals(
                        expectedHashWithoutVersion,
                        sha256,
                        StringComparison.OrdinalIgnoreCase);
                var stateWithoutVersion = NewState(
                    descriptor,
                    identityChangedWithoutVersion
                        ? ToolAvailability.IdentityChanged
                        : ToolAvailability.Available,
                    discovered.ExecutablePath,
                    discovered.PathSource,
                    sha256: sha256);
                return ApplicationResult<ToolDetectionResult>.Succeeded(
                    new ToolDetectionResult(
                        descriptor,
                        stateWithoutVersion,
                        ToolVersionSupportStatus.Unrecognized,
                        identityChangedWithoutVersion
                            ? "tool.identity.changed"
                            : "tool.available.version-metadata-missing"),
                    correlationId);
            }

            var state = NewState(
                descriptor,
                ToolAvailability.Misconfigured,
                discovered.ExecutablePath,
                discovered.PathSource,
                sha256: sha256);
            return ApplicationResult<ToolDetectionResult>.Succeeded(
                new ToolDetectionResult(
                    descriptor,
                    state,
                    ToolVersionSupportStatus.ProbeFailed,
                    probe.DiagnosticCode),
                correlationId);
        }

        var expectedHash = identityBaseline.GetExpectedSha256(
            descriptor.Id,
            discovered.ExecutablePath);
        var identityChanged = !string.IsNullOrWhiteSpace(expectedHash)
            && !string.Equals(expectedHash, sha256, StringComparison.OrdinalIgnoreCase);
        var support = descriptor.SupportedVersions.Evaluate(probe.Version);
        var availability = identityChanged
            ? ToolAvailability.IdentityChanged
            : support == ToolVersionSupportStatus.Supported
                ? ToolAvailability.Available
                : ToolAvailability.UnsupportedVersion;
        var code = identityChanged
            ? "tool.identity.changed"
            : support == ToolVersionSupportStatus.Supported
                ? "tool.available"
                : "tool.version.unsupported";
        var detectedState = NewState(
            descriptor,
            availability,
            discovered.ExecutablePath,
            discovered.PathSource,
            probe.Version,
            sha256,
            probe.Publisher);

        return ApplicationResult<ToolDetectionResult>.Succeeded(
            new ToolDetectionResult(descriptor, detectedState, support, code),
            correlationId);
    }

    private static ToolState NewState(
        ToolDescriptor descriptor,
        ToolAvailability availability,
        string? executablePath,
        ToolPathSource? pathSource,
        string? version = null,
        string? sha256 = null,
        string? publisher = null) =>
        new(
            descriptor.Id,
            availability,
            executablePath,
            pathSource,
            version,
            sha256,
            publisher,
            descriptor.Capabilities,
            descriptor.RequiresElevationForUse);

    private static ApplicationMessage Message(
        string code,
        string textKey,
        string diagnostic,
        ApplicationMessageSeverity severity) =>
        new(code, textKey, diagnostic, severity, Array.Empty<StorageObjectId>());
}
