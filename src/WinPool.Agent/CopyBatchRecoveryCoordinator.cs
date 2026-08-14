using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Testing;

namespace WinPool.Agent;

/// <summary>
/// Owns persisted CopyBatch manifest creation and recovery verification. It
/// deliberately contains no process execution or UI/request-routing concerns.
/// </summary>
internal sealed class CopyBatchRecoveryCoordinator
{
    private readonly CopyBatchRepository copyBatchRepository;

    public CopyBatchRecoveryCoordinator(CopyBatchRepository copyBatchRepository)
    {
        this.copyBatchRepository = copyBatchRepository
            ?? throw new ArgumentNullException(nameof(copyBatchRepository));
    }

    public async Task<CopyBatchManifest?> PrepareAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        if (!IsRegisteredDirectoryCopy(step))
        {
            return null;
        }

        var sourcePath = GetRequiredTextParameter(step, "sourceRelativeDirectory");
        var destinationPath = GetRequiredTextParameter(step, "destinationRelativeDirectory");
        var inspector = new RegisteredTestDirectoryInspector();
        var source = await inspector.CaptureAsync(
            run,
            sourcePath,
            includeHashes: false,
            cancellationToken);
        var destination = await CaptureOrCreateEmptyDirectoryEvidenceAsync(
            run,
            destinationPath,
            inspector,
            cancellationToken);
        var manifest = await copyBatchRepository.GetManifestAsync(
            run.Plan.RunId,
            step.Id,
            cancellationToken);
        if (manifest is null)
        {
            var batchThresholdMiB = GetPositiveIntegerParameter(
                step,
                "copyBatchThresholdMiB",
                128 * 1024,
                1,
                1024 * 1024);
            var maximumFiles = GetPositiveIntegerParameter(
                step,
                "copyBatchMaximumFiles",
                10_000,
                1,
                100_000);
            manifest = new CopyBatchPlanner().Compile(
                run.Plan,
                step.Id,
                source,
                destination,
                checked(batchThresholdMiB * 1024L * 1024L),
                maximumFiles,
                DateTimeOffset.UtcNow);
            await copyBatchRepository.SaveManifestAsync(manifest, cancellationToken);
        }
        else
        {
            if (!StringComparer.Ordinal.Equals(manifest.PlanHash, run.Plan.PlanHash))
            {
                throw new UnauthorizedAccessException(
                    "The persisted copy recovery manifest belongs to a different plan.");
            }

            var report = new CopyBatchPlanner().Recover(manifest, source, destination);
            await copyBatchRepository.ApplyRecoveryReportAsync(
                run.Plan.RunId,
                step.Id,
                report,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (report.ConflictCount > 0)
            {
                throw new InvalidDataException(
                    "Copy recovery found source or destination conflicts; no files were overwritten.");
            }
        }

        return manifest;
    }

    public async Task FinalizeAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        if (!IsRegisteredDirectoryCopy(step))
        {
            return;
        }

        var manifest = await copyBatchRepository.GetManifestAsync(
                run.Plan.RunId,
                step.Id,
                cancellationToken)
            ?? throw new InvalidDataException(
                "The copy step completed without its persisted recovery manifest.");
        var inspector = new RegisteredTestDirectoryInspector();
        var source = await inspector.CaptureAsync(
            run,
            GetRequiredTextParameter(step, "sourceRelativeDirectory"),
            includeHashes: false,
            cancellationToken);
        var destination = await inspector.CaptureAsync(
            run,
            GetRequiredTextParameter(step, "destinationRelativeDirectory"),
            includeHashes: false,
            cancellationToken);
        var report = new CopyBatchPlanner().Recover(manifest, source, destination);
        await copyBatchRepository.ApplyRecoveryReportAsync(
            run.Plan.RunId,
            step.Id,
            report,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (report.PendingCount > 0 || report.ConflictCount > 0)
        {
            throw new InvalidDataException(
                "RoboCopy returned but the persisted copy manifest did not fully match the destination.");
        }
    }

    private static bool IsRegisteredDirectoryCopy(TestStep step) =>
        step.Action is TestActionKind.Copy
        && step.ToolId?.Value is "windows.robocopy"
        && GetTextParameter(step, "sourceRelativeDirectory") is not null
        && GetTextParameter(step, "destinationRelativeDirectory") is not null;

    private static string GetRequiredTextParameter(TestStep step, string key) =>
        GetTextParameter(step, key)
        ?? throw new InvalidDataException($"The copy batch parameter '{key}' is required.");

    private static int GetPositiveIntegerParameter(
        TestStep step,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!step.Parameters.TryGetValue(name, out var parameter))
        {
            return fallback;
        }

        if (parameter.Kind is not TestParameterKind.Integer
            || !int.TryParse(
                parameter.SerializedValue,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException($"The copy batch parameter '{name}' is invalid.");
        }

        return value;
    }

    private static string? GetTextParameter(TestStep step, string key) =>
        step.Parameters.TryGetValue(key, out var parameter)
        && parameter.Kind is TestParameterKind.Text
        && !string.IsNullOrWhiteSpace(parameter.SerializedValue)
            ? parameter.SerializedValue
            : null;

    private static async Task<RegisteredDirectoryEvidence>
        CaptureOrCreateEmptyDirectoryEvidenceAsync(
            AuthorizedTestRun run,
            string relativePath,
            RegisteredTestDirectoryInspector inspector,
            CancellationToken cancellationToken)
    {
        try
        {
            return await inspector.CaptureAsync(
                run,
                relativePath,
                includeHashes: false,
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            var registration = run.Plan.Workspace.RegisteredDirectories.Single(
                item => StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(Path.Combine(
                        run.Plan.Workspace.NormalizedRootDirectory,
                        item.RelativePath)),
                    Path.GetFullPath(Path.Combine(
                        run.Plan.Workspace.NormalizedRootDirectory,
                        relativePath))));
            return new(
                registration.RelativePath,
                registration.IdentityToken,
                registration.MaximumBytes,
                registration.MaximumFileCount,
                0,
                0,
                []);
        }
    }
}
