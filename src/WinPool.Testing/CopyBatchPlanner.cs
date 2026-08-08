using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Testing;

public sealed class CopyBatchPlanner
{
    public static readonly AlgorithmIdentity Algorithm =
        new(
            "ALG-COPY-BATCH-001",
            "1.0.0",
            AlgorithmConfidence.Derived,
            "Plan/04 §6");

    public CopyBatchManifest Compile(
        TestPlan plan,
        string copyStepId,
        RegisteredDirectoryEvidence source,
        RegisteredDirectoryEvidence destination,
        long batchThresholdBytes,
        int maximumFilesPerBatch,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyStepId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!TestPlanCompiler.HasValidHash(plan))
        {
            throw new UnauthorizedAccessException(
                "The copy batch manifest requires an intact test plan hash.");
        }

        if (batchThresholdBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchThresholdBytes));
        }

        if (maximumFilesPerBatch is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFilesPerBatch));
        }

        ValidateEvidenceTotals(source);
        ValidateEvidenceTotals(destination);
        var step = plan.Steps.SingleOrDefault(
                item => StringComparer.Ordinal.Equals(item.Id, copyStepId))
            ?? throw new KeyNotFoundException(
                $"The copy step does not exist: '{copyStepId}'.");
        if (step.Action is not TestActionKind.Copy
            || step.ToolId?.Value is not "windows.robocopy")
        {
            throw new InvalidOperationException(
                "Copy batches currently require a typed RoboCopy directory step.");
        }

        RequireMatchingParameter(
            step,
            "sourceRelativeDirectory",
            source.RelativePath);
        RequireMatchingParameter(
            step,
            "destinationRelativeDirectory",
            destination.RelativePath);
        var sourceRegistration = plan.Workspace.RegisteredDirectories.Single(
            item => SamePath(item.RelativePath, source.RelativePath));
        var destinationRegistration = plan.Workspace.RegisteredDirectories.Single(
            item => SamePath(item.RelativePath, destination.RelativePath));
        if (!StringComparer.Ordinal.Equals(
                sourceRegistration.IdentityToken,
                source.IdentityToken)
            || !StringComparer.Ordinal.Equals(
                destinationRegistration.IdentityToken,
                destination.IdentityToken)
            || source.ActualFileCount > sourceRegistration.MaximumFileCount
            || source.ActualBytes > sourceRegistration.MaximumBytes
            || destination.ActualFileCount != 0
            || destination.ActualBytes != 0)
        {
            throw new UnauthorizedAccessException(
                "The copy batch evidence does not match its registered directory boundaries.");
        }

        var ordered = source.Entries
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Select(item => NormalizeRelative(item.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != ordered.Length)
        {
            throw new InvalidOperationException(
                "The source evidence contains duplicate relative paths.");
        }

        var entries = new List<CopyBatchManifestEntry>(ordered.Length);
        var segments = new List<CopyBatchSegment>();
        var batchNumber = 1;
        var batchBytes = 0L;
        var batchFiles = 0;
        for (var ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            var item = ordered[ordinal];
            var relativePath = NormalizeRelative(item.RelativePath);
            if (item.Length < 0)
            {
                throw new InvalidOperationException(
                    $"The source file length is invalid: '{relativePath}'.");
            }

            if (batchFiles > 0
                && (checked(batchBytes + item.Length) > batchThresholdBytes
                    || batchFiles >= maximumFilesPerBatch))
            {
                segments.Add(new(batchNumber, batchBytes, batchFiles));
                batchNumber++;
                batchBytes = 0;
                batchFiles = 0;
            }

            batchBytes = checked(batchBytes + item.Length);
            batchFiles++;
            entries.Add(
                new(
                    ordinal,
                    batchNumber,
                    relativePath,
                    item.Length,
                    item.LastWriteTimeUtc.UtcTicks,
                    item.Attributes,
                    NormalizeHash(item.Sha256)));
        }

        if (batchFiles > 0)
        {
            segments.Add(new(batchNumber, batchBytes, batchFiles));
        }

        var manifest = new CopyBatchManifest(
            plan.RunId,
            step.Id,
            plan.PlanHash,
            source.IdentityToken,
            destination.IdentityToken,
            batchThresholdBytes,
            maximumFilesPerBatch,
            entries,
            segments,
            Algorithm,
            createdAtUtc,
            string.Empty);
        return manifest with
        {
            ManifestHash = CopyBatchManifestHash.Compute(manifest)
        };
    }

    public CopyBatchRecoveryReport Recover(
        CopyBatchManifest manifest,
        RegisteredDirectoryEvidence source,
        RegisteredDirectoryEvidence destination)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!CopyBatchManifestHash.IsValid(manifest)
            || !StringComparer.Ordinal.Equals(
                manifest.SourceDirectoryIdentity,
                source.IdentityToken)
            || !StringComparer.Ordinal.Equals(
                manifest.DestinationDirectoryIdentity,
                destination.IdentityToken))
        {
            throw new UnauthorizedAccessException(
                "The copy recovery evidence does not match the persisted manifest.");
        }

        ValidateEvidenceTotals(source);
        ValidateEvidenceTotals(destination);
        EnsureUniquePaths(source);
        EnsureUniquePaths(destination);
        var sourceByPath = source.Entries.ToDictionary(
            item => NormalizeRelative(item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var destinationByPath = destination.Entries.ToDictionary(
            item => NormalizeRelative(item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var items = new List<CopyBatchRecoveryItem>(manifest.Entries.Count);
        foreach (var entry in manifest.Entries)
        {
            if (!sourceByPath.TryGetValue(entry.RelativePath, out var sourceEntry)
                || !MatchesManifest(entry, sourceEntry))
            {
                items.Add(
                    new(
                        entry.Ordinal,
                        entry.RelativePath,
                        CopyBatchRecoveryDecision.Conflict,
                        "copy.recovery.source_changed"));
                continue;
            }

            if (!destinationByPath.TryGetValue(
                    entry.RelativePath,
                    out var destinationEntry))
            {
                items.Add(
                    new(
                        entry.Ordinal,
                        entry.RelativePath,
                        CopyBatchRecoveryDecision.Pending,
                        "copy.recovery.target_missing"));
                continue;
            }

            var metadataMatch =
                destinationEntry.Length == entry.Length
                && destinationEntry.LastWriteTimeUtc.UtcTicks
                    == entry.LastWriteTimeUtcTicks
                && destinationEntry.Attributes == entry.Attributes;
            var hashMatch = entry.Sha256 is null
                || StringComparer.OrdinalIgnoreCase.Equals(
                    entry.Sha256,
                    destinationEntry.Sha256);
            items.Add(
                metadataMatch && hashMatch
                    ? new(
                        entry.Ordinal,
                        entry.RelativePath,
                        CopyBatchRecoveryDecision.AcceptCompletedTarget,
                        "copy.recovery.target_accepted")
                    : new(
                        entry.Ordinal,
                        entry.RelativePath,
                        CopyBatchRecoveryDecision.Conflict,
                        "copy.recovery.target_conflict"));
        }

        var manifestPaths = manifest.Entries
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in destination.Entries.Where(
                     item => !manifestPaths.Contains(
                         NormalizeRelative(item.RelativePath))))
        {
            items.Add(
                new(
                    -1,
                    NormalizeRelative(extra.RelativePath),
                    CopyBatchRecoveryDecision.Conflict,
                    "copy.recovery.target_unknown"));
        }

        return new(
            manifest.ManifestHash,
            items.Count(
                item => item.Decision
                    is CopyBatchRecoveryDecision.AcceptCompletedTarget),
            items.Count(
                item => item.Decision is CopyBatchRecoveryDecision.Pending),
            items.Count(
                item => item.Decision is CopyBatchRecoveryDecision.Conflict),
            items);
    }

    public static bool HasValidHash(CopyBatchManifest manifest) =>
        CopyBatchManifestHash.IsValid(manifest);

    private static bool MatchesManifest(
        CopyBatchManifestEntry manifest,
        RegisteredDirectoryEntryEvidence evidence) =>
        manifest.Length == evidence.Length
        && manifest.LastWriteTimeUtcTicks == evidence.LastWriteTimeUtc.UtcTicks
        && manifest.Attributes == evidence.Attributes
        && (manifest.Sha256 is null
            || StringComparer.OrdinalIgnoreCase.Equals(
                manifest.Sha256,
                evidence.Sha256));

    private static void RequireMatchingParameter(
        TestStep step,
        string name,
        string expected)
    {
        if (!step.Parameters.TryGetValue(name, out var parameter)
            || parameter.Kind is not TestParameterKind.Text
            || !SamePath(parameter.SerializedValue, expected))
        {
            throw new UnauthorizedAccessException(
                $"The copy step {name} does not match the captured directory.");
        }
    }

    private static string NormalizeRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new UnauthorizedAccessException(
                "Copy batch paths must remain relative.");
        }

        var normalized = path.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        if (normalized.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
        {
            throw new UnauthorizedAccessException(
                "Copy batch paths cannot escape their registered directory.");
        }

        return normalized;
    }

    private static bool SamePath(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            NormalizeRelative(first),
            NormalizeRelative(second));

    private static string? NormalizeHash(string? hash)
    {
        if (hash is null)
        {
            return null;
        }

        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Copy batch entry hashes must be SHA-256 values.");
        }

        return hash.ToLowerInvariant();
    }

    private static void ValidateEvidenceTotals(
        RegisteredDirectoryEvidence evidence)
    {
        if (evidence.ActualFileCount != evidence.Entries.Count
            || evidence.ActualBytes
                != evidence.Entries.Aggregate(
                    0L,
                    (total, entry) => checked(total + entry.Length)))
        {
            throw new InvalidDataException(
                "The registered directory evidence totals do not match its entries.");
        }
    }

    private static void EnsureUniquePaths(
        RegisteredDirectoryEvidence evidence)
    {
        if (evidence.Entries
            .Select(item => NormalizeRelative(item.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != evidence.Entries.Count)
        {
            throw new InvalidDataException(
                "The registered directory evidence contains duplicate relative paths.");
        }
    }
}
