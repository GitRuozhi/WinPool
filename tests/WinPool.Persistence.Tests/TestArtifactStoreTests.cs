using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class TestArtifactStoreTests
{
    [Fact]
    public async Task SavesRawStandardStreamsAsHashedGzipArtifacts()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "artifact-test-agent");
        var artifacts = new TestArtifactStore(database.Store, lease);
        var runId = TestRunId.New();
        var saved = await artifacts.SaveWorkerOutputAsync(
            runId,
            "step/with unsafe path text",
            [
                Event(runId, WorkerEventKind.StandardOutput, "part-1"),
                Event(runId, WorkerEventKind.StandardError, "error"),
                Event(runId, WorkerEventKind.StandardOutput, "part-2")
            ]);

        Assert.Equal(2, saved.Count);
        var listed = await artifacts.ListRunArtifactsAsync(runId);
        Assert.Equal(2, listed.Count);
        Assert.All(
            listed,
            item =>
            {
                Assert.Equal("test_run", item.OwnerKind);
                Assert.Equal("application/gzip", item.MediaType);
                Assert.Equal(64, item.Sha256.Length);
                Assert.DoesNotContain("unsafe", item.RelativePath);
            });
        var stdout = listed.Single(item => item.RelativePath.Contains(".stdout.", StringComparison.Ordinal));
        var fullPath = Path.Combine(database.Directory, stdout.RelativePath);
        await using var file = File.OpenRead(fullPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var text = new StreamReader(gzip, Encoding.UTF8);
        Assert.Equal("part-1part-2", await text.ReadToEndAsync());
    }

    private static WorkerEvent Event(
        TestRunId runId,
        WorkerEventKind kind,
        string value) =>
        new(
            runId,
            "step/with unsafe path text",
            kind,
            WorkerEventImportance.Output,
            DateTimeOffset.UtcNow,
            "output",
            Encoding.UTF8.GetBytes(value));

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }
        public WinPoolSqliteStore Store { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Persistence.Artifact.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
