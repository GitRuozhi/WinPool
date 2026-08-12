using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class RuntimeRepositoryTests
{
    [Fact]
    public async Task AlgorithmRegistryRoundTripsInStableOrderAndIsVersionImmutable()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var writer = new AlgorithmRegistryRepository(database.Store, lease);
        await writer.RegisterAsync(
            new("ALG-Z", "1.0.0", AlgorithmConfidence.Derived, "Plan/old"));
        await writer.RegisterAsync(
            new("ALG-A", "2.0.0", AlgorithmConfidence.Speculative, "Plan/A"));
        await writer.RegisterAsync(
            new("ALG-Z", "1.0.0", AlgorithmConfidence.Derived, "Plan/old"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.RegisterAsync(
                new("ALG-Z", "1.0.0", AlgorithmConfidence.Proven, "Plan/new")));

        var algorithms = await new AlgorithmRegistryRepository(database.Store)
            .ListAsync();

        Assert.Equal(["ALG-A", "ALG-Z"], algorithms.Select(item => item.Id));
        var updated = algorithms.Single(item => item.Id == "ALG-Z");
        Assert.Equal(AlgorithmConfidence.Derived, updated.Confidence);
        Assert.Equal("Plan/old", updated.EvidenceReference);
    }

    [Fact]
    public async Task AgentSessionRecordsUncleanStartAndCleanEnd()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new AgentSessionRepository(database.Store, lease);
        var unclean = new AgentInstanceId(Guid.NewGuid());
        var clean = new AgentInstanceId(Guid.NewGuid());
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await repository.StartAsync(unclean, 100, started);
        await repository.StartAsync(clean, 101, started.AddSeconds(1));
        await repository.EndAsync(clean, started.AddMinutes(1), shutdownClean: true);

        var sessions = await new AgentSessionRepository(database.Store)
            .ListUncleanAsync(10);

        var remaining = Assert.Single(sessions);
        Assert.Equal(unclean, remaining.SessionId);
        Assert.Null(remaining.EndedAtUtc);
        Assert.False(remaining.ShutdownClean);
    }

    [Fact]
    public async Task AgentStartupRecoveryClosesOnlyOpenSessionsAsUncleanEvidence()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new AgentSessionRepository(database.Store, lease);
        var interrupted = new AgentInstanceId(Guid.NewGuid());
        var clean = new AgentInstanceId(Guid.NewGuid());
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        var recovered = started.AddMinutes(5);
        await repository.StartAsync(interrupted, 100, started);
        await repository.StartAsync(clean, 101, started.AddSeconds(1));
        await repository.EndAsync(clean, started.AddMinutes(1), shutdownClean: true);

        var recoveredSessions = await repository.RecoverOpenSessionsAsync(recovered);
        var unclean = await new AgentSessionRepository(database.Store)
            .ListUncleanAsync(10);

        Assert.Equal(interrupted, Assert.Single(recoveredSessions).SessionId);
        var persisted = Assert.Single(unclean);
        Assert.Equal(interrupted, persisted.SessionId);
        Assert.Equal(recovered, persisted.EndedAtUtc);
        Assert.False(persisted.ShutdownClean);
    }

    [Fact]
    public async Task ReadOnlyRuntimeRepositoriesCannotWrite()
    {
        await using var database = await RuntimeDatabase.CreateAsync();

        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => new AlgorithmRegistryRepository(database.Store).RegisterAsync(
                new(
                    "ALG-TEST",
                    "1.0.0",
                    AlgorithmConfidence.Derived,
                    "test")));
        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => new AgentSessionRepository(database.Store).StartAsync(
                new AgentInstanceId(Guid.NewGuid()),
                1,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ExternalToolStateRoundTripsCustomPathAndAvailability()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var writer = new ExternalToolStateRepository(database.Store, lease);
        var detectedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await writer.SaveAsync(
            new ToolState(
                new ToolId("fio"),
                ToolAvailability.UnsupportedVersion,
                @"D:\Tools\fio.exe",
                ToolPathSource.CustomPath,
                "4.1",
                new string('A', 64),
                null,
                ToolCapabilities.RandomIo,
                false),
            detectedAt);

        var state = await new ExternalToolStateRepository(database.Store)
            .GetAsync(new ToolId("fio"));

        Assert.NotNull(state);
        Assert.Equal(ToolAvailability.UnsupportedVersion, state.Availability);
        Assert.Equal(@"D:\Tools\fio.exe", state.ConfiguredPath);
        Assert.Equal("4.1", state.DetectedVersion);
        Assert.Equal(detectedAt, state.DetectedAtUtc);
    }

    [Fact]
    public async Task WorkerProcessRoundTripsForAgentSession()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessionId = new AgentInstanceId(Guid.NewGuid());
        var sessions = new AgentSessionRepository(database.Store, lease);
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await sessions.StartAsync(sessionId, 10, started);
        var expected = new ProcessRegistration(
            ProcessInstanceId.New(),
            42,
            WorkerKind.Test,
            CorrelationId.New(),
            started,
            started.AddSeconds(5),
            SupervisedProcessState.Running,
            OwnsJobObject: true,
            ShutdownDeadlineUtc: null);
        var writer = new WorkerProcessRepository(database.Store, lease);
        Assert.Equal(
            WorkerProcessSaveResult.Applied,
            await writer.SaveAsync(sessionId, expected));

        var actual = Assert.Single(
            await new WorkerProcessRepository(database.Store).ListAsync(sessionId));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WorkerProcessTerminalStateRejectsLateHeartbeat()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessionId = new AgentInstanceId(Guid.NewGuid());
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await new AgentSessionRepository(database.Store, lease)
            .StartAsync(sessionId, 10, started);
        var running = new ProcessRegistration(
            ProcessInstanceId.New(),
            42,
            WorkerKind.Test,
            CorrelationId.New(),
            started,
            started.AddSeconds(5),
            SupervisedProcessState.Running,
            OwnsJobObject: true,
            ShutdownDeadlineUtc: null);
        var repository = new WorkerProcessRepository(database.Store, lease);
        Assert.Equal(
            WorkerProcessSaveResult.Applied,
            await repository.SaveAsync(sessionId, running));
        var exited = running with
        {
            State = SupervisedProcessState.Exited,
            LastHeartbeatUtc = started.AddSeconds(10)
        };
        Assert.Equal(
            WorkerProcessSaveResult.Applied,
            await repository.SaveAsync(sessionId, exited));

        var lateHeartbeat = running with
        {
            LastHeartbeatUtc = started.AddMinutes(1)
        };
        Assert.Equal(
            WorkerProcessSaveResult.IgnoredStale,
            await repository.SaveAsync(sessionId, lateHeartbeat));

        var stored = Assert.Single(await new WorkerProcessRepository(database.Store)
            .ListAsync(sessionId));
        Assert.Equal(SupervisedProcessState.Exited, stored.State);
        Assert.Equal(exited.LastHeartbeatUtc, stored.LastHeartbeatUtc);

        Assert.Equal(
            WorkerProcessSaveResult.IgnoredStale,
            await repository.SaveAsync(
                sessionId,
                exited with { LastHeartbeatUtc = started.AddMinutes(2) }));
        stored = Assert.Single(await new WorkerProcessRepository(database.Store)
            .ListAsync(sessionId));
        Assert.Equal(exited.LastHeartbeatUtc, stored.LastHeartbeatUtc);
    }

    [Fact]
    public async Task WorkerProcessSameStateWritesKeepHeartbeatMonotonic()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessionId = new AgentInstanceId(Guid.NewGuid());
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await new AgentSessionRepository(database.Store, lease)
            .StartAsync(sessionId, 10, started);
        var running = CreateRunningRegistration(started);
        var repository = new WorkerProcessRepository(database.Store, lease);

        Assert.Equal(WorkerProcessSaveResult.Applied, await repository.SaveAsync(sessionId, running));
        Assert.Equal(
            WorkerProcessSaveResult.Applied,
            await repository.SaveAsync(
                sessionId,
                running with { LastHeartbeatUtc = started.AddSeconds(10) }));
        Assert.Equal(
            WorkerProcessSaveResult.Applied,
            await repository.SaveAsync(
                sessionId,
                running with { LastHeartbeatUtc = started.AddSeconds(5) }));

        var stored = Assert.Single(await new WorkerProcessRepository(database.Store)
            .ListAsync(sessionId));
        Assert.Equal(started.AddSeconds(10), stored.LastHeartbeatUtc);
    }

    [Fact]
    public async Task WorkerProcessRejectsIdentityMutationWithoutChangingStoredProcess()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessionId = new AgentInstanceId(Guid.NewGuid());
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await new AgentSessionRepository(database.Store, lease)
            .StartAsync(sessionId, 10, started);
        var running = CreateRunningRegistration(started);
        var repository = new WorkerProcessRepository(database.Store, lease);
        Assert.Equal(WorkerProcessSaveResult.Applied, await repository.SaveAsync(sessionId, running));

        var otherSessionId = new AgentInstanceId(Guid.NewGuid());
        foreach (var mutation in new[]
                 {
                     (Session: sessionId, Registration: running with { ProcessId = running.ProcessId + 1 }),
                     (Session: otherSessionId, Registration: running),
                     (Session: sessionId, Registration: running with { Kind = WorkerKind.Inventory }),
                     (Session: sessionId, Registration: running with { CorrelationId = CorrelationId.New() }),
                     (Session: sessionId, Registration: running with
                     {
                         StartedAtUtc = started.AddSeconds(1),
                         LastHeartbeatUtc = started.AddSeconds(6)
                     }),
                     (Session: sessionId, Registration: running with { OwnsJobObject = false })
                 })
        {
            Assert.Equal(
                WorkerProcessSaveResult.RejectedIdentityMismatch,
                await repository.SaveAsync(mutation.Session, mutation.Registration));
        }

        var stored = Assert.Single(await new WorkerProcessRepository(database.Store)
            .ListAsync(sessionId));
        Assert.Equal(running, stored);
    }

    [Fact]
    public async Task WorkerProcessStoppingDeadlineIsEstablishedOnceAndCannotBeOverwritten()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessionId = new AgentInstanceId(Guid.NewGuid());
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await new AgentSessionRepository(database.Store, lease)
            .StartAsync(sessionId, 10, started);
        var running = CreateRunningRegistration(started);
        var repository = new WorkerProcessRepository(database.Store, lease);
        Assert.Equal(WorkerProcessSaveResult.Applied, await repository.SaveAsync(sessionId, running));
        var deadline = started.AddMinutes(1);
        var stopping = running with
        {
            State = SupervisedProcessState.Stopping,
            LastHeartbeatUtc = started.AddSeconds(10),
            ShutdownDeadlineUtc = deadline
        };
        Assert.Equal(WorkerProcessSaveResult.Applied, await repository.SaveAsync(sessionId, stopping));

        Assert.Equal(
            WorkerProcessSaveResult.IgnoredStale,
            await repository.SaveAsync(
                sessionId,
                stopping with { ShutdownDeadlineUtc = deadline.AddMinutes(1) }));
        Assert.Equal(
            WorkerProcessSaveResult.IgnoredStale,
            await repository.SaveAsync(sessionId, running with { LastHeartbeatUtc = started.AddMinutes(2) }));
        Assert.Equal(
            WorkerProcessSaveResult.Applied,
            await repository.SaveAsync(
                sessionId,
                stopping with
                {
                    State = SupervisedProcessState.Exited,
                    LastHeartbeatUtc = started.AddSeconds(20)
                }));

        var stored = Assert.Single(await new WorkerProcessRepository(database.Store)
            .ListAsync(sessionId));
        Assert.Equal(SupervisedProcessState.Exited, stored.State);
        Assert.Equal(deadline, stored.ShutdownDeadlineUtc);
    }

    [Fact]
    public async Task StorageHealthEventsPersistDeduplicateAndReturnInTimeOrder()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new StorageHealthEventRepository(database.Store, lease);
        var first = new StorageHealthEvent(
            "Microsoft-Windows-StorageSpaces-Driver/Operational",
            "Microsoft-Windows-StorageSpaces-Driver",
            100,
            311,
            StorageHealthEventSeverity.Error,
            DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000),
            "Virtual disk requires attention.");
        var second = first with
        {
            RecordId = 101,
            EventId = 312,
            OccurredAtUtc = first.OccurredAtUtc.AddSeconds(1),
            Severity = StorageHealthEventSeverity.Warning
        };

        await repository.AddAsync(second, CancellationToken.None);
        await repository.AddAsync(first, CancellationToken.None);
        await repository.AddAsync(first, CancellationToken.None);
        var events = await repository.ListRecentAsync(10, CancellationToken.None);

        Assert.Equal([first, second], events);
    }

    private sealed class RuntimeDatabase : IAsyncDisposable
    {
        private RuntimeDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }

        public WinPoolSqliteStore Store { get; }

        public static async Task<RuntimeDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.RuntimePersistence.Tests",
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

    private static ProcessRegistration CreateRunningRegistration(DateTimeOffset started) =>
        new(
            ProcessInstanceId.New(),
            42,
            WorkerKind.Test,
            CorrelationId.New(),
            started,
            started.AddSeconds(5),
            SupervisedProcessState.Running,
            OwnsJobObject: true,
            ShutdownDeadlineUtc: null);
}
