using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedDiteLegacyImport(
    Guid ImportId,
    string SourceFileName,
    string SourceSha256,
    DateTimeOffset ImportedAtUtc,
    int RunCount,
    int MetricCount);

public sealed record DiteLegacyImportSaveResult(
    PersistedDiteLegacyImport Import,
    bool AlreadyExisted);

public sealed class DiteLegacyImportRepository
{
    private const int MaximumRuns = 200_000;
    private const int MaximumMetrics = 5_000_000;
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public DiteLegacyImportRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public DiteLegacyImportRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner
            ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<DiteLegacyImportSaveResult> SaveAsync(
        DiteLegacyImportResult import,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken = default)
    {
        Validate(import);
        AssertWriteOwnership();
        var importId = Guid.NewGuid();
        var persistedId = importId.ToString("N");
        var sourceFileName = Path.GetFileName(import.SourceFileName);
        var sourceSha256 = import.SourceSha256.ToLowerInvariant();

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var importCommand = connection.CreateCommand();
        importCommand.Transaction = transaction;
        importCommand.CommandText = """
            INSERT INTO legacy_test_imports(
                import_id, source_file_name, source_sha256,
                format_version, imported_at_utc_ms)
            VALUES($id, $file, $sha, 'dite-v23-v24-wide-csv', $imported)
            ON CONFLICT(source_sha256) DO NOTHING;
            """;
        importCommand.Parameters.AddWithValue("$id", persistedId);
        importCommand.Parameters.AddWithValue("$file", sourceFileName);
        importCommand.Parameters.AddWithValue("$sha", sourceSha256);
        importCommand.Parameters.AddWithValue(
            "$imported",
            importedAtUtc.ToUnixTimeMilliseconds());
        var inserted = await importCommand.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await GetBySha256Async(sourceSha256, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The existing Dite import could not be read after a hash conflict.");
            return new(existing, AlreadyExisted: true);
        }

        await using var runCommand = connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText = """
            INSERT INTO legacy_test_runs(
                import_id, run_ordinal, test_time, drive,
                tool, profile, log_file_name)
            VALUES($import, $ordinal, $time, $drive, $tool, $profile, $log);
            """;
        var runImport = runCommand.Parameters.Add("$import", SqliteType.Text);
        var ordinal = runCommand.Parameters.Add("$ordinal", SqliteType.Integer);
        var testTime = runCommand.Parameters.Add("$time", SqliteType.Text);
        var drive = runCommand.Parameters.Add("$drive", SqliteType.Text);
        var tool = runCommand.Parameters.Add("$tool", SqliteType.Text);
        var profile = runCommand.Parameters.Add("$profile", SqliteType.Text);
        var log = runCommand.Parameters.Add("$log", SqliteType.Text);
        runCommand.Prepare();

        await using var metricCommand = connection.CreateCommand();
        metricCommand.Transaction = transaction;
        metricCommand.CommandText = """
            INSERT INTO legacy_test_metrics(
                import_id, run_ordinal, metric_name, metric_value, unit)
            VALUES($import, $ordinal, $name, $value, $unit);
            """;
        var metricImport = metricCommand.Parameters.Add("$import", SqliteType.Text);
        var metricOrdinal = metricCommand.Parameters.Add("$ordinal", SqliteType.Integer);
        var metricName = metricCommand.Parameters.Add("$name", SqliteType.Text);
        var metricValue = metricCommand.Parameters.Add("$value", SqliteType.Real);
        var metricUnit = metricCommand.Parameters.Add("$unit", SqliteType.Text);
        metricCommand.Prepare();

        var metricCount = 0;
        for (var index = 0; index < import.Runs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = import.Runs[index];
            runImport.Value = persistedId;
            ordinal.Value = index;
            testTime.Value = run.TestTime.Trim();
            drive.Value = run.Drive.Trim();
            tool.Value = run.Tool.Trim();
            profile.Value = run.Profile.Trim();
            log.Value = string.IsNullOrWhiteSpace(run.LogFileName)
                ? DBNull.Value
                : Path.GetFileName(run.LogFileName.Trim());
            await runCommand.ExecuteNonQueryAsync(cancellationToken);

            foreach (var metric in run.Metrics)
            {
                metricImport.Value = persistedId;
                metricOrdinal.Value = index;
                metricName.Value = metric.MetricId.Trim();
                metricValue.Value = metric.Value;
                metricUnit.Value = metric.Unit.Trim();
                await metricCommand.ExecuteNonQueryAsync(cancellationToken);
                metricCount++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            new(
                importId,
                sourceFileName,
                sourceSha256,
                importedAtUtc,
                import.Runs.Count,
                metricCount),
            AlreadyExisted: false);
    }

    public async Task<PersistedDiteLegacyImport?> GetBySha256Async(
        string sourceSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.import_id, i.source_file_name, i.source_sha256,
                   i.imported_at_utc_ms,
                   (SELECT COUNT(*) FROM legacy_test_runs r
                    WHERE r.import_id = i.import_id),
                   (SELECT COUNT(*) FROM legacy_test_metrics m
                    WHERE m.import_id = i.import_id)
            FROM legacy_test_imports i
            WHERE i.source_sha256 = $sha;
            """;
        command.Parameters.AddWithValue("$sha", sourceSha256.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new(
            Guid.ParseExact(reader.GetString(0), "N"),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            checked((int)reader.GetInt64(4)),
            checked((int)reader.GetInt64(5)));
    }

    public async Task<IReadOnlyList<PersistedDiteLegacyImport>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.import_id, i.source_file_name, i.source_sha256,
                   i.imported_at_utc_ms,
                   (SELECT COUNT(*) FROM legacy_test_runs r
                    WHERE r.import_id = i.import_id),
                   (SELECT COUNT(*) FROM legacy_test_metrics m
                    WHERE m.import_id = i.import_id)
            FROM legacy_test_imports i
            ORDER BY i.imported_at_utc_ms DESC, i.import_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<PersistedDiteLegacyImport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadMetadata(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<DiteLegacyMetricSummary>?> GetSummariesAsync(
        Guid importId,
        CancellationToken cancellationToken = default)
    {
        if (importId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(importId));
        }

        var persistedId = importId.ToString("N");
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM legacy_test_imports WHERE import_id=$id;";
            exists.Parameters.AddWithValue("$id", persistedId);
            if (Convert.ToInt64(
                    await exists.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metric_name, unit, metric_value
            FROM legacy_test_metrics
            WHERE import_id = $id
            ORDER BY metric_name, unit, metric_value;
            """;
        command.Parameters.AddWithValue("$id", persistedId);
        var groups = new Dictionary<(string Name, string Unit), List<double>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups.Add(key, values);
            }

            values.Add(reader.GetDouble(2));
        }

        return groups
            .Select(pair =>
            {
                var values = pair.Value;
                return new DiteLegacyMetricSummary(
                    pair.Key.Name,
                    pair.Key.Unit,
                    values.Count,
                    values[0],
                    Median(values),
                    values[^1]);
            })
            .OrderBy(item => item.MetricId, StringComparer.Ordinal)
            .ThenBy(item => item.Unit, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Validate(DiteLegacyImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);
        var sourceFileName = Path.GetFileName(import.SourceFileName);
        if (string.IsNullOrWhiteSpace(sourceFileName)
            || sourceFileName.Length > 260
            || import.SourceSha256.Length != 64
            || import.SourceSha256.Any(character => !Uri.IsHexDigit(character))
            || import.Runs.Count is <= 0 or > MaximumRuns)
        {
            throw new InvalidDataException("The Dite import metadata is invalid.");
        }

        var metricCount = 0L;
        foreach (var run in import.Runs)
        {
            if (!ValidText(run.TestTime, 512)
                || !ValidText(run.Drive, 512)
                || !ValidText(run.Tool, 512)
                || !ValidText(run.Profile, 4096)
                || run.LogFileName is { Length: > 4096 }
                || run.Metrics.GroupBy(metric => metric.MetricId)
                    .Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("A Dite import run is invalid.");
            }

            foreach (var metric in run.Metrics)
            {
                if (!ValidText(metric.MetricId, 512)
                    || !ValidText(metric.Unit, 64)
                    || !double.IsFinite(metric.Value))
                {
                    throw new InvalidDataException("A Dite import metric is invalid.");
                }
            }

            metricCount += run.Metrics.Count;
            if (metricCount > MaximumMetrics)
            {
                throw new InvalidDataException(
                    "The Dite import exceeds the metric-count safety limit.");
            }
        }
    }

    private static bool ValidText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static PersistedDiteLegacyImport ReadMetadata(SqliteDataReader reader) =>
        new(
            Guid.ParseExact(reader.GetString(0), "N"),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            checked((int)reader.GetInt64(4)),
            checked((int)reader.GetInt64(5)));

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2d;

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }
}
