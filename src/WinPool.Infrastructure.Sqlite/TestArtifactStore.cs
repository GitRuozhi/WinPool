using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedArtifact(
    Guid ArtifactId,
    string OwnerKind,
    string OwnerId,
    string RelativePath,
    string Sha256,
    long ByteLength,
    string MediaType,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Stores bounded raw worker output below the private WinPool data directory.
/// Artifact paths are generated from run/step identities and never supplied by IPC.
/// </summary>
public sealed class TestArtifactStore
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly string dataRoot;

    public TestArtifactStore(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
        dataRoot = Path.GetDirectoryName(store.DatabasePath)
                   ?? throw new InvalidOperationException(
                       "The WinPool database path has no parent directory.");
    }

    public async Task<IReadOnlyList<PersistedArtifact>> SaveWorkerOutputAsync(
        TestRunId runId,
        string stepId,
        IReadOnlyList<WorkerEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(events);
        var artifacts = new List<PersistedArtifact>();
        foreach (var stream in new[]
                 {
                     WorkerEventKind.StandardOutput,
                     WorkerEventKind.StandardError
                 })
        {
            var chunks = events
                .Where(item => item.Kind == stream && !item.RawBytes.IsEmpty)
                .ToArray();
            if (chunks.Length == 0)
            {
                continue;
            }

            artifacts.Add(
                await SaveStreamAsync(
                    runId,
                    stepId,
                    stream,
                    chunks,
                    cancellationToken));
        }

        return artifacts;
    }

    public async Task<IReadOnlyList<PersistedArtifact>> ListRunArtifactsAsync(
        TestRunId runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, owner_kind, owner_id, relative_path, sha256,
                   byte_length, media_type, created_at_utc_ms
            FROM artifacts
            WHERE owner_kind = 'test_run' AND owner_id = $owner
            ORDER BY created_at_utc_ms, artifact_id;
            """;
        command.Parameters.AddWithValue("$owner", Id(runId));
        var results = new List<PersistedArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))));
        }

        return results;
    }

    public async Task<PersistedArtifact> SaveGeneratedArtifactAsync(
        TestRunId runId,
        string logicalName,
        string mediaType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (content.IsEmpty)
        {
            throw new ArgumentException(
                "A generated evidence artifact cannot be empty.",
                nameof(content));
        }

        writeOwner.AssertOwnership(store);
        var artifactId = Guid.NewGuid();
        var nameToken = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(logicalName)))
            .ToLowerInvariant()[..24];
        var extension = string.Equals(
            mediaType,
            "application/json",
            StringComparison.OrdinalIgnoreCase)
                ? ".json"
                : ".bin";
        var relativePath = Path.Combine(
            "artifacts",
            "test-runs",
            runId.Value.ToString("N"),
            $"{nameToken}.{artifactId:N}{extension}");
        var finalPath = Path.Combine(dataRoot, relativePath);
        var parent = Path.GetDirectoryName(finalPath)
                     ?? throw new InvalidOperationException(
                         "The artifact path has no parent directory.");
        Directory.CreateDirectory(parent);
        var stagingPath = Path.Combine(parent, $".{artifactId:N}.staging");
        await using (var file = new FileStream(
                         stagingPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await file.WriteAsync(content, cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
        }

        File.Move(stagingPath, finalPath, overwrite: false);
        var sha256 = Convert.ToHexString(SHA256.HashData(content.Span))
            .ToLowerInvariant();
        var artifact = new PersistedArtifact(
            artifactId,
            "test_run",
            Id(runId),
            relativePath,
            sha256,
            content.Length,
            mediaType.Trim(),
            DateTimeOffset.UtcNow);
        await InsertAsync(artifact, cancellationToken);
        return artifact;
    }

    private async Task<PersistedArtifact> SaveStreamAsync(
        TestRunId runId,
        string stepId,
        WorkerEventKind stream,
        IReadOnlyList<WorkerEvent> chunks,
        CancellationToken cancellationToken)
    {
        writeOwner.AssertOwnership(store);
        var artifactId = Guid.NewGuid();
        var stepToken = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(stepId)))
            .ToLowerInvariant()[..24];
        var streamName = stream == WorkerEventKind.StandardOutput
            ? "stdout"
            : "stderr";
        var relativePath = Path.Combine(
            "artifacts",
            "test-runs",
            runId.Value.ToString("N"),
            $"{stepToken}.{streamName}.{artifactId:N}.bin.gz");
        var finalPath = Path.Combine(dataRoot, relativePath);
        var parent = Path.GetDirectoryName(finalPath)
                     ?? throw new InvalidOperationException(
                         "The artifact path has no parent directory.");
        Directory.CreateDirectory(parent);
        var stagingPath = Path.Combine(
            parent,
            $".{artifactId:N}.staging");
        await using (var file = new FileStream(
                         stagingPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var gzip = new GZipStream(
                         file,
                         CompressionLevel.SmallestSize,
                         leaveOpen: false))
        {
            foreach (var chunk in chunks)
            {
                await gzip.WriteAsync(chunk.RawBytes, cancellationToken);
            }
        }

        File.Move(stagingPath, finalPath, overwrite: false);
        string sha256;
        await using (var file = new FileStream(
                         finalPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(file, cancellationToken))
                .ToLowerInvariant();
        }

        var created = DateTimeOffset.UtcNow;
        var artifact = new PersistedArtifact(
            artifactId,
            "test_run",
            Id(runId),
            relativePath,
            sha256,
            new FileInfo(finalPath).Length,
            "application/gzip",
            created);
        await InsertAsync(artifact, cancellationToken);
        return artifact;
    }

    private async Task InsertAsync(
        PersistedArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifacts(
                artifact_id, owner_kind, owner_id, relative_path, sha256,
                byte_length, media_type, created_at_utc_ms)
            VALUES($artifact, $kind, $owner, $path, $sha, $length, $media, $created);
            """;
        command.Parameters.AddWithValue("$artifact", artifact.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$kind", artifact.OwnerKind);
        command.Parameters.AddWithValue("$owner", artifact.OwnerId);
        command.Parameters.AddWithValue("$path", artifact.RelativePath);
        command.Parameters.AddWithValue("$sha", artifact.Sha256);
        command.Parameters.AddWithValue("$length", artifact.ByteLength);
        command.Parameters.AddWithValue("$media", artifact.MediaType);
        command.Parameters.AddWithValue(
            "$created",
            artifact.CreatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Id(TestRunId runId) =>
        runId.Value.ToString("N");
}
