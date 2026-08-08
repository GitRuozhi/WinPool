namespace WinPool.Application;

public enum ElevatedBrokerOperationKind
{
    CleanTemporaryFiles,
    ClearSystemFileCache,
    FlushVolume,
    TrimOrOptimizeVolume,
    SetActivePowerPlan,
    InstallMsiTool
}

public sealed record MsiToolInstallSnapshot(
    ToolId ToolId,
    string PackageRelativePath,
    string Sha256);

public sealed record MsiToolInstallEvidence(int ExitCode, bool RebootRequired);

public interface IMsiToolInstallPort
{
    Task<MsiToolInstallEvidence> InstallAsync(
        MsiToolInstallSnapshot package,
        CancellationToken cancellationToken);
}

public sealed record ElevatedBrokerExecutionRequest(
    Guid Nonce,
    Guid AgentSessionId,
    int AgentProcessId,
    string UserSidHash,
    string PlanHash,
    DateTimeOffset ExpiresAtUtc,
    ElevatedBrokerOperationKind Operation,
    IReadOnlyList<TemporaryCleanupCandidate>? TemporaryCleanupCandidates = null,
    VolumeTargetSnapshot? VolumeTarget = null,
    Guid? PowerPlanId = null,
    RamMapCacheClearMode? RamMapMode = null,
    RamMapToolIdentity? PlannedRamMapIdentity = null,
    MsiToolInstallSnapshot? MsiToolInstall = null);

public sealed record ElevatedBrokerExecutionResult(
    ElevatedBrokerOperationKind Operation,
    bool Succeeded,
    string Code,
    IReadOnlyList<TemporaryCleanupItemResult>? TemporaryCleanupItems = null,
    VolumeMaintenanceEvidence? VolumeEvidence = null,
    RamMapCacheClearEvidence? RamMapEvidence = null,
    MsiToolInstallEvidence? MsiInstallEvidence = null);

public static class ElevatedBrokerExecutionValidator
{
    public const int MaximumTemporaryCleanupCandidates = 2_000;

    public static string? Validate(
        ElevatedBrokerExecutionRequest request,
        Guid expectedNonce,
        Guid expectedAgentSessionId,
        int expectedAgentProcessId,
        string expectedUserSidHash,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Nonce == Guid.Empty || request.Nonce != expectedNonce)
        {
            return "broker.request.nonce-mismatch";
        }

        if (request.AgentSessionId == Guid.Empty ||
            request.AgentSessionId != expectedAgentSessionId ||
            request.AgentProcessId <= 0 ||
            request.AgentProcessId != expectedAgentProcessId)
        {
            return "broker.request.agent-mismatch";
        }

        if (!StringComparer.Ordinal.Equals(request.UserSidHash, expectedUserSidHash))
        {
            return "broker.request.user-mismatch";
        }

        if (request.ExpiresAtUtc <= nowUtc ||
            request.ExpiresAtUtc - nowUtc > TimeSpan.FromMinutes(2))
        {
            return "broker.request.expired";
        }

        if (string.IsNullOrWhiteSpace(request.PlanHash) || request.PlanHash.Length > 256)
        {
            return "broker.request.plan-hash-invalid";
        }

        return request.Operation switch
        {
            ElevatedBrokerOperationKind.CleanTemporaryFiles =>
                ValidateTemporaryCleanup(request),
            ElevatedBrokerOperationKind.ClearSystemFileCache =>
                ValidateRamMap(request),
            ElevatedBrokerOperationKind.FlushVolume or
                ElevatedBrokerOperationKind.TrimOrOptimizeVolume =>
                ValidateVolume(request),
            ElevatedBrokerOperationKind.SetActivePowerPlan =>
                ValidatePowerPlan(request),
            ElevatedBrokerOperationKind.InstallMsiTool =>
                ValidateMsiInstall(request),
            _ => "broker.request.operation-not-whitelisted"
        };
    }

    private static string? ValidateTemporaryCleanup(ElevatedBrokerExecutionRequest request)
    {
        if (request.TemporaryCleanupCandidates is null ||
            request.TemporaryCleanupCandidates.Count == 0 ||
            request.TemporaryCleanupCandidates.Count >
                MaximumTemporaryCleanupCandidates ||
            request.VolumeTarget is not null ||
            request.PowerPlanId is not null ||
            request.RamMapMode is not null ||
            request.PlannedRamMapIdentity is not null ||
            request.MsiToolInstall is not null)
        {
            return "broker.request.temporary-cleanup-invalid";
        }

        return request.TemporaryCleanupCandidates.Any(
            candidate =>
                string.IsNullOrWhiteSpace(candidate.Id.Value) ||
                string.IsNullOrWhiteSpace(candidate.FullPath) ||
                !Path.IsPathFullyQualified(candidate.FullPath) ||
                candidate.Length < 0 ||
                candidate.IsReparsePoint ||
                candidate.IsWindowsResourceProtected)
            ? "broker.request.temporary-cleanup-candidate-invalid"
            : null;
    }

    private static string? ValidateVolume(ElevatedBrokerExecutionRequest request)
    {
        if (request.VolumeTarget is null ||
            string.IsNullOrWhiteSpace(request.VolumeTarget.StableIdentity) ||
            string.IsNullOrWhiteSpace(request.VolumeTarget.DisplayIdentity) ||
            request.TemporaryCleanupCandidates is not null ||
            request.PowerPlanId is not null ||
            request.RamMapMode is not null ||
            request.PlannedRamMapIdentity is not null ||
            request.MsiToolInstall is not null)
        {
            return "broker.request.volume-invalid";
        }

        return null;
    }

    private static string? ValidatePowerPlan(ElevatedBrokerExecutionRequest request) =>
        !request.PowerPlanId.HasValue ||
        request.PowerPlanId.Value == Guid.Empty ||
        request.TemporaryCleanupCandidates is not null ||
        request.VolumeTarget is not null ||
        request.RamMapMode is not null ||
        request.PlannedRamMapIdentity is not null ||
        request.MsiToolInstall is not null
            ? "broker.request.power-plan-invalid"
            : null;

    private static string? ValidateRamMap(ElevatedBrokerExecutionRequest request)
    {
        if (request.RamMapMode != RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList ||
            request.PlannedRamMapIdentity is null ||
            request.TemporaryCleanupCandidates is not null ||
            request.VolumeTarget is not null ||
            request.PowerPlanId is not null ||
            request.MsiToolInstall is not null)
        {
            return "broker.request.rammap-invalid";
        }

        var identity = request.PlannedRamMapIdentity;
        return string.IsNullOrWhiteSpace(identity.PathBindingHash) ||
               string.IsNullOrWhiteSpace(identity.Version) ||
               string.IsNullOrWhiteSpace(identity.Publisher) ||
               identity.Sha256.Length != 64 ||
               !identity.SignatureTrusted
            ? "broker.request.rammap-identity-invalid"
            : null;
    }

    private static string? ValidateMsiInstall(ElevatedBrokerExecutionRequest request)
    {
        var package = request.MsiToolInstall;
        if (package is null ||
            package.ToolId.Value != "fio" ||
            package.Sha256.Length != 64 ||
            package.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            Path.IsPathFullyQualified(package.PackageRelativePath) ||
            package.PackageRelativePath.Contains("..", StringComparison.Ordinal) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                package.PackageRelativePath.Replace('\\', '/'),
                $"tool-downloads/{package.Sha256.ToLowerInvariant()}.msi") ||
            request.TemporaryCleanupCandidates is not null ||
            request.VolumeTarget is not null ||
            request.PowerPlanId is not null ||
            request.RamMapMode is not null ||
            request.PlannedRamMapIdentity is not null)
        {
            return "broker.request.msi-install-invalid";
        }

        return null;
    }
}

public sealed record ElevatedBrokerExecutionPorts(
    ITemporaryFileCleanupPort TemporaryFiles,
    ITemporaryCleanupPathPolicy TemporaryPathPolicy,
    IRamMapCacheClearPort RamMap,
    IVolumeMaintenancePort Volumes,
    ITemporaryPowerPlanPort PowerPlans,
    IMsiToolInstallPort? MsiInstaller = null);

public sealed class ElevatedBrokerDispatcher
{
    private readonly ElevatedBrokerExecutionPorts _ports;
    private readonly Guid _expectedNonce;
    private readonly Guid _expectedAgentSessionId;
    private readonly int _expectedAgentProcessId;
    private readonly string _expectedUserSidHash;
    private readonly TimeProvider _timeProvider;

    public ElevatedBrokerDispatcher(
        ElevatedBrokerExecutionPorts ports,
        Guid expectedNonce,
        Guid expectedAgentSessionId,
        int expectedAgentProcessId,
        string expectedUserSidHash,
        TimeProvider? timeProvider = null)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        if (expectedNonce == Guid.Empty ||
            expectedAgentSessionId == Guid.Empty ||
            expectedAgentProcessId <= 0 ||
            string.IsNullOrWhiteSpace(expectedUserSidHash))
        {
            throw new ArgumentException("The elevated Broker identity is incomplete.");
        }

        _expectedNonce = expectedNonce;
        _expectedAgentSessionId = expectedAgentSessionId;
        _expectedAgentProcessId = expectedAgentProcessId;
        _expectedUserSidHash = expectedUserSidHash;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ElevatedBrokerExecutionResult> ExecuteAsync(
        ElevatedBrokerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var rejection = ElevatedBrokerExecutionValidator.Validate(
            request,
            _expectedNonce,
            _expectedAgentSessionId,
            _expectedAgentProcessId,
            _expectedUserSidHash,
            _timeProvider.GetUtcNow());
        if (rejection is not null)
        {
            return Rejected(request.Operation, rejection);
        }

        try
        {
            return request.Operation switch
            {
                ElevatedBrokerOperationKind.CleanTemporaryFiles =>
                    await CleanTemporaryFilesAsync(request, cancellationToken).ConfigureAwait(false),
                ElevatedBrokerOperationKind.ClearSystemFileCache =>
                    await ClearRamMapAsync(request, cancellationToken).ConfigureAwait(false),
                ElevatedBrokerOperationKind.FlushVolume =>
                    await MaintainVolumeAsync(request, optimize: false, cancellationToken)
                        .ConfigureAwait(false),
                ElevatedBrokerOperationKind.TrimOrOptimizeVolume =>
                    await MaintainVolumeAsync(request, optimize: true, cancellationToken)
                        .ConfigureAwait(false),
                ElevatedBrokerOperationKind.SetActivePowerPlan =>
                    await SetPowerPlanAsync(request, cancellationToken).ConfigureAwait(false),
                ElevatedBrokerOperationKind.InstallMsiTool =>
                    await InstallMsiAsync(request, cancellationToken).ConfigureAwait(false),
                _ => Rejected(request.Operation, "broker.request.operation-not-whitelisted")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Rejected(request.Operation, "broker.execution.failed");
        }
    }

    private async Task<ElevatedBrokerExecutionResult> CleanTemporaryFilesAsync(
        ElevatedBrokerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = request.TemporaryCleanupCandidates!;
        if (candidates.Any(candidate => !_ports.TemporaryPathPolicy.Evaluate(candidate).IsAllowed))
        {
            return Rejected(request.Operation, "broker.temporary-cleanup.policy-rejected");
        }

        var result = await _ports.TemporaryFiles
            .CleanAsync(candidates, cancellationToken)
            .ConfigureAwait(false);
        return new(
            request.Operation,
            result.Items.All(item => item.Status != TemporaryCleanupItemStatus.Failed),
            "broker.temporary-cleanup.completed",
            result.Items);
    }

    private async Task<ElevatedBrokerExecutionResult> ClearRamMapAsync(
        ElevatedBrokerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var current = await _ports.RamMap
            .DetectIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null || !SameRamMapIdentity(current, request.PlannedRamMapIdentity!))
        {
            return Rejected(request.Operation, "broker.rammap.identity-changed");
        }

        var evidence = await _ports.RamMap
            .ClearAsync(
                new RamMapCacheClearRequest(
                    request.RamMapMode!.Value,
                    request.PlanHash,
                    RequiresElevatedBroker: false),
                cancellationToken)
            .ConfigureAwait(false);
        if (evidence.ExitCode != 0 ||
            !evidence.Arguments.SequenceEqual(["-Es", "-Et"]))
        {
            return Rejected(request.Operation, "broker.rammap.execution-invalid");
        }

        evidence = evidence with { UsedElevatedBroker = true };
        return new(
            request.Operation,
            true,
            "broker.rammap.completed",
            RamMapEvidence: evidence);
    }

    private async Task<ElevatedBrokerExecutionResult> MaintainVolumeAsync(
        ElevatedBrokerExecutionRequest request,
        bool optimize,
        CancellationToken cancellationToken)
    {
        var expected = request.VolumeTarget!;
        var current = await _ports.Volumes
            .ResolveCurrentTargetAsync(expected.VolumeId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null ||
            !StringComparer.Ordinal.Equals(current.StableIdentity, expected.StableIdentity) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                current.DisplayIdentity,
                expected.DisplayIdentity))
        {
            return Rejected(request.Operation, "broker.volume.identity-changed");
        }

        var evidence = optimize
            ? await _ports.Volumes.TrimOrOptimizeAsync(current, cancellationToken)
                .ConfigureAwait(false)
            : await _ports.Volumes.FlushAsync(current, cancellationToken)
                .ConfigureAwait(false);
        return new(
            request.Operation,
            true,
            optimize
                ? "broker.volume.optimize-completed"
                : "broker.volume.flush-completed",
            VolumeEvidence: evidence);
    }

    private async Task<ElevatedBrokerExecutionResult> SetPowerPlanAsync(
        ElevatedBrokerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        await _ports.PowerPlans
            .ActivateAsync(request.PowerPlanId!.Value, cancellationToken)
            .ConfigureAwait(false);
        return new(request.Operation, true, "broker.power-plan.activated");
    }

    private async Task<ElevatedBrokerExecutionResult> InstallMsiAsync(
        ElevatedBrokerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (_ports.MsiInstaller is null)
        {
            return Rejected(request.Operation, "broker.msi-install.unavailable");
        }

        var evidence = await _ports.MsiInstaller
            .InstallAsync(request.MsiToolInstall!, cancellationToken)
            .ConfigureAwait(false);
        var succeeded = evidence.ExitCode is 0 or 1641 or 3010;
        return new(
            request.Operation,
            succeeded,
            succeeded ? "broker.msi-install.completed" : "broker.msi-install.failed",
            MsiInstallEvidence: evidence);
    }

    private static bool SameRamMapIdentity(
        RamMapToolIdentity current,
        RamMapToolIdentity planned) =>
        current.SignatureTrusted &&
        planned.SignatureTrusted &&
        StringComparer.Ordinal.Equals(current.PathBindingHash, planned.PathBindingHash) &&
        StringComparer.Ordinal.Equals(current.Sha256, planned.Sha256) &&
        StringComparer.Ordinal.Equals(current.Version, planned.Version) &&
        StringComparer.Ordinal.Equals(current.Publisher, planned.Publisher);

    private static ElevatedBrokerExecutionResult Rejected(
        ElevatedBrokerOperationKind operation,
        string code) =>
        new(operation, false, code);
}
