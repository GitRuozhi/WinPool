using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed class CopyBatchRepository : ICopyBatchCheckpointStore
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public CopyBatchRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public CopyBatchRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner
            ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<bool> SaveManifestAsync(
        CopyBatchManifest manifest,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT manifest_hash
            FROM copy_batch_manifests
            WHERE run_id=$run AND step_id=$step;
            """;
        existing.Parameters.AddWithValue("$run", Id(manifest.RunId));
        existing.Parameters.AddWithValue("$step", manifest.StepId);
        var existingHash = await existing.ExecuteScalarAsync(cancellationToken)
            as string;
        if (existingHash is not null)
        {
            if (!StringComparer.Ordinal.Equals(
                    existingHash,
                    manifest.ManifestHash))
            {
                throw new InvalidOperationException(
                    "A different copy batch manifest is already bound to this test step.");
            }

            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await InsertManifestAsync(
            connection,
            transaction,
            manifest,
            cancellationToken);
        await InsertBatchesAsync(
            connection,
            transaction,
            manifest,
            cancellationToken);
        await InsertEntriesAsync(
            connection,
            transaction,
            manifest,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<CopyBatchManifest?> GetManifestAsync(
        TestRunId runId,
        string stepId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var header = connection.CreateCommand();
        header.CommandText = """
            SELECT plan_hash, manifest_hash, source_identity,
                   destination_identity, batch_threshold_bytes,
                   maximum_files_per_batch, algorithm_id,
                   algorithm_version, algorithm_confidence,
                   algorithm_reference, created_at_utc_ms
            FROM copy_batch_manifests
            WHERE run_id=$run AND step_id=$step;
            """;
        header.Parameters.AddWithValue("$run", Id(runId));
        header.Parameters.AddWithValue("$step", stepId.Trim());
        await using var reader = await header.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var planHash = reader.GetString(0);
        var manifestHash = reader.GetString(1);
        var sourceIdentity = reader.GetString(2);
        var destinationIdentity = reader.GetString(3);
        var batchThresholdBytes = reader.GetInt64(4);
        var maximumFilesPerBatch = reader.GetInt32(5);
        var algorithm = new AlgorithmIdentity(
            reader.GetString(6),
            reader.GetString(7),
            (AlgorithmConfidence)reader.GetInt32(8),
            reader.GetString(9));
        var createdAtUtc =
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10));
        await reader.DisposeAsync();

        var batches = await ReadBatchesAsync(
            connection,
            runId,
            stepId,
            cancellationToken);
        var entries = await ReadEntriesAsync(
            connection,
            runId,
            stepId,
            cancellationToken);
        var manifest = new CopyBatchManifest(
            runId,
            stepId.Trim(),
            planHash,
            sourceIdentity,
            destinationIdentity,
            batchThresholdBytes,
            maximumFilesPerBatch,
            entries,
            batches,
            algorithm,
            createdAtUtc,
            manifestHash);
        if (!CopyBatchManifestHash.IsValid(manifest))
        {
            throw new InvalidDataException(
                "The persisted copy batch manifest hash is invalid.");
        }

        return manifest;
    }

    public async Task<IReadOnlyList<CopyBatchEntryCheckpoint>>
        ListEntryCheckpointsAsync(
            TestRunId runId,
            string stepId,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ordinal, state, attempts, last_exit_code,
                   diagnostic_code, updated_at_utc_ms
            FROM copy_batch_entries
            WHERE run_id=$run AND step_id=$step
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$run", Id(runId));
        command.Parameters.AddWithValue("$step", stepId.Trim());
        var results = new List<CopyBatchEntryCheckpoint>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    runId,
                    stepId.Trim(),
                    reader.GetInt32(0),
                    (CopyBatchEntryState)reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(
                        reader.GetInt64(5))));
        }

        return results;
    }

    public async Task UpdateEntryCheckpointAsync(
        CopyBatchEntryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.StepId);
        if (checkpoint.Ordinal < 0
            || checkpoint.Attempts < 0
            || !Enum.IsDefined(checkpoint.State))
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var current = await ReadCurrentEntryAsync(
            connection,
            transaction,
            checkpoint,
            cancellationToken);
        ValidateTransition(current.State, checkpoint.State);
        if (checkpoint.Attempts < current.Attempts
            || checkpoint.State is CopyBatchEntryState.Copying
            && checkpoint.Attempts != current.Attempts + 1
            || checkpoint.State is not CopyBatchEntryState.Copying
            && checkpoint.Attempts != current.Attempts)
        {
            throw new InvalidOperationException(
                "Copy entry attempts must increase exactly when entering Copying.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE copy_batch_entries
            SET state=$state, attempts=$attempts,
                last_exit_code=$exit, diagnostic_code=$code,
                updated_at_utc_ms=$updated
            WHERE run_id=$run AND step_id=$step AND ordinal=$ordinal;
            """;
        update.Parameters.AddWithValue("$state", (int)checkpoint.State);
        update.Parameters.AddWithValue("$attempts", checkpoint.Attempts);
        update.Parameters.AddWithValue(
            "$exit",
            checkpoint.LastExitCode is { } exit ? exit : DBNull.Value);
        update.Parameters.AddWithValue(
            "$code",
            string.IsNullOrWhiteSpace(checkpoint.DiagnosticCode)
                ? DBNull.Value
                : checkpoint.DiagnosticCode.Trim());
        update.Parameters.AddWithValue(
            "$updated",
            checkpoint.UpdatedAtUtc.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$run", Id(checkpoint.RunId));
        update.Parameters.AddWithValue("$step", checkpoint.StepId.Trim());
        update.Parameters.AddWithValue("$ordinal", checkpoint.Ordinal);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException(
                "The copy batch entry no longer exists.");
        }

        await UpdateBatchStateAsync(
            connection,
            transaction,
            checkpoint,
            current.BatchNumber,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkPendingEntriesCopyingAsync(
        TestRunId runId,
        string stepId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using (var batches = connection.CreateCommand())
        {
            batches.Transaction = transaction;
            batches.CommandText = """
                UPDATE copy_batches
                SET state=$running,
                    started_at_utc_ms=COALESCE(started_at_utc_ms, $started),
                    ended_at_utc_ms=NULL,
                    end_reason_code=NULL
                WHERE run_id=$run AND step_id=$step
                  AND EXISTS(
                      SELECT 1 FROM copy_batch_entries entry
                      WHERE entry.run_id=copy_batches.run_id
                        AND entry.step_id=copy_batches.step_id
                        AND entry.batch_number=copy_batches.batch_number
                        AND entry.state IN ($pending, $failed));
                """;
            batches.Parameters.AddWithValue(
                "$running",
                (int)CopyBatchState.Running);
            batches.Parameters.AddWithValue(
                "$started",
                startedAtUtc.ToUnixTimeMilliseconds());
            batches.Parameters.AddWithValue("$run", Id(runId));
            batches.Parameters.AddWithValue("$step", stepId.Trim());
            batches.Parameters.AddWithValue(
                "$pending",
                (int)CopyBatchEntryState.Pending);
            batches.Parameters.AddWithValue(
                "$failed",
                (int)CopyBatchEntryState.Failed);
            await batches.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var entries = connection.CreateCommand())
        {
            entries.Transaction = transaction;
            entries.CommandText = """
                UPDATE copy_batch_entries
                SET state=$copying, attempts=attempts+1,
                    last_exit_code=NULL,
                    diagnostic_code='copy.started',
                    updated_at_utc_ms=$started
                WHERE run_id=$run AND step_id=$step
                  AND state IN ($pending, $failed);
                """;
            entries.Parameters.AddWithValue(
                "$copying",
                (int)CopyBatchEntryState.Copying);
            entries.Parameters.AddWithValue(
                "$started",
                startedAtUtc.ToUnixTimeMilliseconds());
            entries.Parameters.AddWithValue("$run", Id(runId));
            entries.Parameters.AddWithValue("$step", stepId.Trim());
            entries.Parameters.AddWithValue(
                "$pending",
                (int)CopyBatchEntryState.Pending);
            entries.Parameters.AddWithValue(
                "$failed",
                (int)CopyBatchEntryState.Failed);
            await entries.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkEntriesCopyingAsync(
        TestRunId runId,
        string stepId,
        IReadOnlyCollection<int> ordinals,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(ordinals);
        var requested = ordinals.Distinct().Order().ToArray();
        if (requested.Length == 0
            || requested.Length != ordinals.Count
            || requested.Any(item => item < 0)
            || requested.Length > 512)
        {
            throw new ArgumentException(
                "A copy dispatch must contain 1-512 unique non-negative ordinals.",
                nameof(ordinals));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using (var entry = connection.CreateCommand())
        {
            entry.Transaction = transaction;
            entry.CommandText = """
                UPDATE copy_batch_entries
                SET state=$copying, attempts=attempts+1,
                    last_exit_code=NULL, diagnostic_code='copy.started',
                    updated_at_utc_ms=$started
                WHERE run_id=$run AND step_id=$step AND ordinal=$ordinal
                  AND state IN ($pending, $failed);
                """;
            entry.Parameters.AddWithValue(
                "$copying",
                (int)CopyBatchEntryState.Copying);
            entry.Parameters.AddWithValue(
                "$started",
                startedAtUtc.ToUnixTimeMilliseconds());
            entry.Parameters.AddWithValue("$run", Id(runId));
            entry.Parameters.AddWithValue("$step", stepId.Trim());
            var ordinal = entry.Parameters.Add("$ordinal", SqliteType.Integer);
            entry.Parameters.AddWithValue(
                "$pending",
                (int)CopyBatchEntryState.Pending);
            entry.Parameters.AddWithValue(
                "$failed",
                (int)CopyBatchEntryState.Failed);
            entry.Prepare();
            foreach (var item in requested)
            {
                ordinal.Value = item;
                if (await entry.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        $"Copy entry {item} is missing or is not dispatchable.");
                }
            }
        }

        await using (var batches = connection.CreateCommand())
        {
            batches.Transaction = transaction;
            batches.CommandText = """
                UPDATE copy_batches
                SET state=$running,
                    started_at_utc_ms=COALESCE(started_at_utc_ms, $started),
                    ended_at_utc_ms=NULL, end_reason_code=NULL
                WHERE run_id=$run AND step_id=$step
                  AND EXISTS(
                      SELECT 1 FROM copy_batch_entries entry
                      WHERE entry.run_id=copy_batches.run_id
                        AND entry.step_id=copy_batches.step_id
                        AND entry.batch_number=copy_batches.batch_number
                        AND entry.state=$copying);
                """;
            batches.Parameters.AddWithValue(
                "$running",
                (int)CopyBatchState.Running);
            batches.Parameters.AddWithValue(
                "$started",
                startedAtUtc.ToUnixTimeMilliseconds());
            batches.Parameters.AddWithValue("$run", Id(runId));
            batches.Parameters.AddWithValue("$step", stepId.Trim());
            batches.Parameters.AddWithValue(
                "$copying",
                (int)CopyBatchEntryState.Copying);
            await batches.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyRecoveryReportAsync(
        TestRunId runId,
        string stepId,
        CopyBatchRecoveryReport report,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(report);
        if (report.ManifestHash.Length != 64
            || report.ManifestHash.Any(character => !Uri.IsHexDigit(character))
            || report.AcceptedCompletedCount
                != report.Items.Count(
                    item => item.Decision
                        is CopyBatchRecoveryDecision.AcceptCompletedTarget)
            || report.PendingCount
                != report.Items.Count(
                    item => item.Decision
                        is CopyBatchRecoveryDecision.Pending)
            || report.ConflictCount
                != report.Items.Count(
                    item => item.Decision
                        is CopyBatchRecoveryDecision.Conflict))
        {
            throw new InvalidDataException(
                "The copy recovery report summary is invalid.");
        }

        var known = report.Items.Where(item => item.Ordinal >= 0).ToArray();
        if (known.Select(item => item.Ordinal).Distinct().Count() != known.Length)
        {
            throw new InvalidDataException(
                "The copy recovery report contains duplicate ordinals.");
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using (var manifest = connection.CreateCommand())
        {
            manifest.Transaction = transaction;
            manifest.CommandText = """
                SELECT manifest_hash, COUNT(entry.ordinal)
                FROM copy_batch_manifests manifest
                LEFT JOIN copy_batch_entries entry
                  ON entry.run_id=manifest.run_id
                 AND entry.step_id=manifest.step_id
                WHERE manifest.run_id=$run AND manifest.step_id=$step
                GROUP BY manifest.manifest_hash;
                """;
            manifest.Parameters.AddWithValue("$run", Id(runId));
            manifest.Parameters.AddWithValue("$step", stepId.Trim());
            await using var reader = await manifest.ExecuteReaderAsync(
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !StringComparer.Ordinal.Equals(
                    reader.GetString(0),
                    report.ManifestHash)
                || reader.GetInt32(1) != known.Length)
            {
                throw new InvalidDataException(
                    "The copy recovery report does not match the persisted manifest.");
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE copy_batch_entries
                SET state=$state, diagnostic_code=$code,
                    updated_at_utc_ms=$updated
                WHERE run_id=$run AND step_id=$step AND ordinal=$ordinal;
                """;
            var state = update.Parameters.Add("$state", SqliteType.Integer);
            var code = update.Parameters.Add("$code", SqliteType.Text);
            var updated = update.Parameters.Add("$updated", SqliteType.Integer);
            var run = update.Parameters.Add("$run", SqliteType.Text);
            var step = update.Parameters.Add("$step", SqliteType.Text);
            var ordinal = update.Parameters.Add("$ordinal", SqliteType.Integer);
            update.Prepare();
            foreach (var item in known.OrderBy(item => item.Ordinal))
            {
                state.Value = (int)(item.Decision switch
                {
                    CopyBatchRecoveryDecision.Pending =>
                        CopyBatchEntryState.Pending,
                    CopyBatchRecoveryDecision.AcceptCompletedTarget =>
                        CopyBatchEntryState.Completed,
                    CopyBatchRecoveryDecision.Conflict =>
                        CopyBatchEntryState.Conflict,
                    _ => throw new ArgumentOutOfRangeException(nameof(report))
                });
                code.Value = item.Code;
                updated.Value = recoveredAtUtc.ToUnixTimeMilliseconds();
                run.Value = Id(runId);
                step.Value = stepId.Trim();
                ordinal.Value = item.Ordinal;
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidDataException(
                        "The recovery report referenced an unknown copy entry.");
                }
            }
        }

        await using (var batches = connection.CreateCommand())
        {
            batches.Transaction = transaction;
            batches.CommandText = """
                UPDATE copy_batches
                SET state=CASE
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries entry
                            WHERE entry.run_id=copy_batches.run_id
                              AND entry.step_id=copy_batches.step_id
                              AND entry.batch_number=copy_batches.batch_number
                              AND entry.state IN ($conflict, $failed_final))
                            THEN $failed
                        WHEN NOT EXISTS(
                            SELECT 1 FROM copy_batch_entries entry
                            WHERE entry.run_id=copy_batches.run_id
                              AND entry.step_id=copy_batches.step_id
                              AND entry.batch_number=copy_batches.batch_number
                              AND entry.state != $completed)
                            THEN $batch_completed
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries entry
                            WHERE entry.run_id=copy_batches.run_id
                              AND entry.step_id=copy_batches.step_id
                              AND entry.batch_number=copy_batches.batch_number
                              AND entry.state=$copying)
                            THEN $running
                        ELSE $pending_batch
                    END,
                    ended_at_utc_ms=CASE
                        WHEN NOT EXISTS(
                            SELECT 1 FROM copy_batch_entries entry
                            WHERE entry.run_id=copy_batches.run_id
                              AND entry.step_id=copy_batches.step_id
                              AND entry.batch_number=copy_batches.batch_number
                              AND entry.state NOT IN ($completed, $conflict, $failed_final))
                            THEN $updated
                        ELSE NULL
                    END,
                    end_reason_code=CASE
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries entry
                            WHERE entry.run_id=copy_batches.run_id
                              AND entry.step_id=copy_batches.step_id
                              AND entry.batch_number=copy_batches.batch_number
                              AND entry.state IN ($conflict, $failed_final))
                            THEN 'copy.batch.completed_with_failures'
                        WHEN NOT EXISTS(
                            SELECT 1 FROM copy_batch_entries entry
                            WHERE entry.run_id=copy_batches.run_id
                              AND entry.step_id=copy_batches.step_id
                              AND entry.batch_number=copy_batches.batch_number
                              AND entry.state != $completed)
                            THEN 'copy.batch.completed'
                        ELSE NULL
                    END
                WHERE run_id=$run AND step_id=$step;
                """;
            batches.Parameters.AddWithValue(
                "$conflict",
                (int)CopyBatchEntryState.Conflict);
            batches.Parameters.AddWithValue(
                "$failed_final",
                (int)CopyBatchEntryState.FailedFinal);
            batches.Parameters.AddWithValue(
                "$completed",
                (int)CopyBatchEntryState.Completed);
            batches.Parameters.AddWithValue(
                "$copying",
                (int)CopyBatchEntryState.Copying);
            batches.Parameters.AddWithValue(
                "$failed",
                (int)CopyBatchState.Failed);
            batches.Parameters.AddWithValue(
                "$batch_completed",
                (int)CopyBatchState.Completed);
            batches.Parameters.AddWithValue(
                "$running",
                (int)CopyBatchState.Running);
            batches.Parameters.AddWithValue(
                "$pending_batch",
                (int)CopyBatchState.Pending);
            batches.Parameters.AddWithValue(
                "$updated",
                recoveredAtUtc.ToUnixTimeMilliseconds());
            batches.Parameters.AddWithValue("$run", Id(runId));
            batches.Parameters.AddWithValue("$step", stepId.Trim());
            await batches.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkOpenBatchInterruptedAsync(
        TestRunId runId,
        string stepId,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var batches = connection.CreateCommand();
        batches.Transaction = transaction;
        batches.CommandText = """
            UPDATE copy_batches
            SET state=$interrupted, ended_at_utc_ms=$recovered,
                end_reason_code='copy.batch.interrupted'
            WHERE run_id=$run AND step_id=$step AND state=$running;
            """;
        batches.Parameters.AddWithValue(
            "$interrupted",
            (int)CopyBatchState.Interrupted);
        batches.Parameters.AddWithValue("$recovered", recoveredAtUtc.ToUnixTimeMilliseconds());
        batches.Parameters.AddWithValue("$run", Id(runId));
        batches.Parameters.AddWithValue("$step", stepId.Trim());
        batches.Parameters.AddWithValue("$running", (int)CopyBatchState.Running);
        await batches.ExecuteNonQueryAsync(cancellationToken);

        await using var entries = connection.CreateCommand();
        entries.Transaction = transaction;
        entries.CommandText = """
            UPDATE copy_batch_entries
            SET state=$pending,
                diagnostic_code='copy.recovery.interrupted',
                updated_at_utc_ms=$recovered
            WHERE run_id=$run AND step_id=$step AND state=$copying;
            """;
        entries.Parameters.AddWithValue("$pending", (int)CopyBatchEntryState.Pending);
        entries.Parameters.AddWithValue("$recovered", recoveredAtUtc.ToUnixTimeMilliseconds());
        entries.Parameters.AddWithValue("$run", Id(runId));
        entries.Parameters.AddWithValue("$step", stepId.Trim());
        entries.Parameters.AddWithValue("$copying", (int)CopyBatchEntryState.Copying);
        await entries.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertManifestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CopyBatchManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO copy_batch_manifests(
                run_id, step_id, plan_hash, manifest_hash,
                source_identity, destination_identity,
                batch_threshold_bytes, maximum_files_per_batch,
                algorithm_id, algorithm_version, algorithm_confidence,
                algorithm_reference, created_at_utc_ms)
            VALUES(
                $run, $step, $plan, $manifest, $source, $destination,
                $bytes, $files, $algorithm, $version, $confidence,
                $reference, $created);
            """;
        command.Parameters.AddWithValue("$run", Id(manifest.RunId));
        command.Parameters.AddWithValue("$step", manifest.StepId);
        command.Parameters.AddWithValue("$plan", manifest.PlanHash);
        command.Parameters.AddWithValue("$manifest", manifest.ManifestHash);
        command.Parameters.AddWithValue("$source", manifest.SourceDirectoryIdentity);
        command.Parameters.AddWithValue(
            "$destination",
            manifest.DestinationDirectoryIdentity);
        command.Parameters.AddWithValue("$bytes", manifest.BatchThresholdBytes);
        command.Parameters.AddWithValue("$files", manifest.MaximumFilesPerBatch);
        command.Parameters.AddWithValue("$algorithm", manifest.Algorithm.Id);
        command.Parameters.AddWithValue("$version", manifest.Algorithm.Version);
        command.Parameters.AddWithValue(
            "$confidence",
            (int)manifest.Algorithm.Confidence);
        command.Parameters.AddWithValue(
            "$reference",
            manifest.Algorithm.EvidenceReference);
        command.Parameters.AddWithValue(
            "$created",
            manifest.CreatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertBatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CopyBatchManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO copy_batches(
                run_id, step_id, batch_number, state,
                planned_bytes, planned_file_count)
            VALUES($run, $step, $batch, $state, $bytes, $files);
            """;
        var run = command.Parameters.Add("$run", SqliteType.Text);
        var step = command.Parameters.Add("$step", SqliteType.Text);
        var batch = command.Parameters.Add("$batch", SqliteType.Integer);
        var state = command.Parameters.Add("$state", SqliteType.Integer);
        var bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
        var files = command.Parameters.Add("$files", SqliteType.Integer);
        command.Prepare();
        foreach (var item in manifest.Batches)
        {
            run.Value = Id(manifest.RunId);
            step.Value = manifest.StepId;
            batch.Value = item.BatchNumber;
            state.Value = (int)CopyBatchState.Pending;
            bytes.Value = item.PlannedBytes;
            files.Value = item.PlannedFileCount;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CopyBatchManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO copy_batch_entries(
                run_id, step_id, ordinal, batch_number, relative_path,
                length_bytes, last_write_utc_ticks, attributes, sha256,
                state, attempts, updated_at_utc_ms)
            VALUES(
                $run, $step, $ordinal, $batch, $path, $length,
                $last_write, $attributes, $sha, $state, 0, $updated);
            """;
        var run = command.Parameters.Add("$run", SqliteType.Text);
        var step = command.Parameters.Add("$step", SqliteType.Text);
        var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var batch = command.Parameters.Add("$batch", SqliteType.Integer);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var length = command.Parameters.Add("$length", SqliteType.Integer);
        var lastWrite = command.Parameters.Add("$last_write", SqliteType.Integer);
        var attributes = command.Parameters.Add("$attributes", SqliteType.Integer);
        var sha = command.Parameters.Add("$sha", SqliteType.Text);
        var state = command.Parameters.Add("$state", SqliteType.Integer);
        var updated = command.Parameters.Add("$updated", SqliteType.Integer);
        command.Prepare();
        foreach (var item in manifest.Entries)
        {
            run.Value = Id(manifest.RunId);
            step.Value = manifest.StepId;
            ordinal.Value = item.Ordinal;
            batch.Value = item.BatchNumber;
            path.Value = item.RelativePath;
            length.Value = item.Length;
            lastWrite.Value = item.LastWriteTimeUtcTicks;
            attributes.Value = (int)item.Attributes;
            sha.Value = item.Sha256 is null ? DBNull.Value : item.Sha256;
            state.Value = (int)CopyBatchEntryState.Pending;
            updated.Value = manifest.CreatedAtUtc.ToUnixTimeMilliseconds();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<CopyBatchSegment>> ReadBatchesAsync(
        SqliteConnection connection,
        TestRunId runId,
        string stepId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch_number, planned_bytes, planned_file_count
            FROM copy_batches
            WHERE run_id=$run AND step_id=$step
            ORDER BY batch_number;
            """;
        command.Parameters.AddWithValue("$run", Id(runId));
        command.Parameters.AddWithValue("$step", stepId.Trim());
        var results = new List<CopyBatchSegment>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2)));
        }

        return results;
    }

    private static async Task<IReadOnlyList<CopyBatchManifestEntry>>
        ReadEntriesAsync(
            SqliteConnection connection,
            TestRunId runId,
            string stepId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ordinal, batch_number, relative_path, length_bytes,
                   last_write_utc_ticks, attributes, sha256
            FROM copy_batch_entries
            WHERE run_id=$run AND step_id=$step
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$run", Id(runId));
        command.Parameters.AddWithValue("$step", stepId.Trim());
        var results = new List<CopyBatchManifestEntry>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    (FileAttributes)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return results;
    }

    private static async Task<(CopyBatchEntryState State, int Attempts, int BatchNumber)>
        ReadCurrentEntryAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CopyBatchEntryCheckpoint checkpoint,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state, attempts, batch_number
            FROM copy_batch_entries
            WHERE run_id=$run AND step_id=$step AND ordinal=$ordinal;
            """;
        command.Parameters.AddWithValue("$run", Id(checkpoint.RunId));
        command.Parameters.AddWithValue("$step", checkpoint.StepId.Trim());
        command.Parameters.AddWithValue("$ordinal", checkpoint.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException(
                "The copy batch entry does not exist.");
        }

        return (
            (CopyBatchEntryState)reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task UpdateBatchStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CopyBatchEntryCheckpoint checkpoint,
        int batchNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (checkpoint.State is CopyBatchEntryState.Copying)
        {
            command.CommandText = """
                UPDATE copy_batches
                SET state=$running,
                    started_at_utc_ms=COALESCE(started_at_utc_ms, $updated),
                    ended_at_utc_ms=NULL,
                    end_reason_code=NULL
                WHERE run_id=$run AND step_id=$step
                  AND batch_number=$batch;
                """;
            command.Parameters.AddWithValue(
                "$running",
                (int)CopyBatchState.Running);
        }
        else
        {
            command.CommandText = """
                UPDATE copy_batches
                SET state=CASE
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries
                            WHERE run_id=$run AND step_id=$step
                              AND batch_number=$batch
                              AND state NOT IN ($completed, $conflict, $failed_final))
                            THEN state
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries
                            WHERE run_id=$run AND step_id=$step
                              AND batch_number=$batch
                              AND state IN ($conflict, $failed_final))
                            THEN $failed
                        ELSE $batch_completed
                    END,
                    ended_at_utc_ms=CASE
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries
                            WHERE run_id=$run AND step_id=$step
                              AND batch_number=$batch
                              AND state NOT IN ($completed, $conflict, $failed_final))
                            THEN ended_at_utc_ms
                        ELSE $updated
                    END,
                    end_reason_code=CASE
                        WHEN EXISTS(
                            SELECT 1 FROM copy_batch_entries
                            WHERE run_id=$run AND step_id=$step
                              AND batch_number=$batch
                              AND state IN ($conflict, $failed_final))
                            THEN 'copy.batch.completed_with_failures'
                        WHEN NOT EXISTS(
                            SELECT 1 FROM copy_batch_entries
                            WHERE run_id=$run AND step_id=$step
                              AND batch_number=$batch
                              AND state NOT IN ($completed, $conflict, $failed_final))
                            THEN 'copy.batch.completed'
                        ELSE end_reason_code
                    END
                WHERE run_id=$run AND step_id=$step
                  AND batch_number=$batch;
                """;
            command.Parameters.AddWithValue(
                "$completed",
                (int)CopyBatchEntryState.Completed);
            command.Parameters.AddWithValue(
                "$conflict",
                (int)CopyBatchEntryState.Conflict);
            command.Parameters.AddWithValue(
                "$failed_final",
                (int)CopyBatchEntryState.FailedFinal);
            command.Parameters.AddWithValue(
                "$failed",
                (int)CopyBatchState.Failed);
            command.Parameters.AddWithValue(
                "$batch_completed",
                (int)CopyBatchState.Completed);
        }

        command.Parameters.AddWithValue(
            "$updated",
            checkpoint.UpdatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$run", Id(checkpoint.RunId));
        command.Parameters.AddWithValue("$step", checkpoint.StepId.Trim());
        command.Parameters.AddWithValue("$batch", batchNumber);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateManifest(CopyBatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.StepId);
        if (!CopyBatchManifestHash.IsValid(manifest)
            || manifest.BatchThresholdBytes <= 0
            || manifest.MaximumFilesPerBatch <= 0
            || manifest.Entries.Count == 0
            || manifest.Batches.Count == 0
            || manifest.Entries.Select(item => item.Ordinal)
                .SequenceEqual(Enumerable.Range(0, manifest.Entries.Count))
                is false
            || manifest.Batches.Select(item => item.BatchNumber)
                .SequenceEqual(Enumerable.Range(1, manifest.Batches.Count))
                is false)
        {
            throw new InvalidDataException(
                "The copy batch manifest is invalid or has been modified.");
        }

        var batches = manifest.Batches.ToDictionary(item => item.BatchNumber);
        var groups = manifest.Entries
            .GroupBy(item => item.BatchNumber)
            .ToArray();
        if (groups.Length != batches.Count)
        {
            throw new InvalidDataException(
                "The copy batch manifest contains an empty or unknown batch.");
        }

        foreach (var group in groups)
        {
            if (!batches.TryGetValue(group.Key, out var batch)
                || group.Count() != batch.PlannedFileCount
                || group.Sum(item => item.Length) != batch.PlannedBytes)
            {
                throw new InvalidDataException(
                    "The copy batch manifest totals do not match its entries.");
            }
        }
    }

    private static void ValidateTransition(
        CopyBatchEntryState current,
        CopyBatchEntryState next)
    {
        var allowed = current switch
        {
            CopyBatchEntryState.Pending =>
                next is CopyBatchEntryState.Pending
                    or CopyBatchEntryState.Copying
                    or CopyBatchEntryState.Failed
                    or CopyBatchEntryState.Completed
                    or CopyBatchEntryState.Conflict,
            CopyBatchEntryState.Copying =>
                next is CopyBatchEntryState.Completed
                    or CopyBatchEntryState.Failed
                    or CopyBatchEntryState.Conflict,
            CopyBatchEntryState.Failed =>
                next is CopyBatchEntryState.Copying
                    or CopyBatchEntryState.Pending
                    or CopyBatchEntryState.FailedFinal
                    or CopyBatchEntryState.Conflict,
            CopyBatchEntryState.Completed =>
                next is CopyBatchEntryState.Completed,
            CopyBatchEntryState.Conflict =>
                next is CopyBatchEntryState.Conflict
                    or CopyBatchEntryState.Pending,
            CopyBatchEntryState.FailedFinal =>
                next is CopyBatchEntryState.FailedFinal
                    or CopyBatchEntryState.Pending,
            _ => false
        };
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"The copy entry transition {current} -> {next} is not allowed.");
        }
    }

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }

    private static string Id(TestRunId runId) =>
        OperationPlanRepository.Id(runId.Value);
}
