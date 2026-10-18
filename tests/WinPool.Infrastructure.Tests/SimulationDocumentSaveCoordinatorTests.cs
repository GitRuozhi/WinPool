using System.Collections.Concurrent;
using System.Diagnostics;
using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class SimulationDocumentSaveCoordinatorTests
{
    [Fact]
    public async Task TenLocalActionsCoalesceWithoutRefreezingIdsOrTime()
    {
        var saved = new ConcurrentQueue<SimulationEditCommit>();
        using var saves = new SimulationDocumentSaveCoordinator((commit, _) =>
        { saved.Enqueue(commit); return Task.FromResult(commit.Document); });
        var local = CreateDocument("coalesced");
        var frozen = new List<SimulationEditRequest>();
        for (var index = 0; index < 10; index++)
        {
            var edit = SimulationEditIntent.Freeze(new SimulationEditRequest(SimulationEditKind.Rename,
                local.Snapshot.Computer.StableId, Name: $"name-{index}"), $"edit-{index}", DateTimeOffset.UnixEpoch.AddSeconds(index));
            frozen.Add(edit);
            local = saves.Accept(new(local with { DisplayName = $"name-{index}", Revision = local.Revision + 1 },
                new(local.Id, local.Revision, $"local-{index}", [edit])));
            Assert.Equal(2, local.Revision);
            await Task.Delay(50);
        }
        await saves.FlushAsync(local.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var batch = Assert.Single(saved);
        Assert.Equal(frozen, batch.Intent.Edits);
        Assert.Equal(1, batch.Intent.ExpectedRevision);
        Assert.Equal(2, batch.Document.Revision);
        Assert.Equal("name-9", batch.Document.DisplayName);
        Assert.True(saves.GetState(local.Id)!.IsDurable);
    }

    [Fact]
    public async Task SlowPrefixDoesNotOverwriteNewLocalContentAndOnlyOneSaveIsInFlight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<SimulationEditCommit>();
        var active = 0; var maximum = 0;
        using var saves = new SimulationDocumentSaveCoordinator(async (commit, _) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            batches.Enqueue(commit); started.TrySetResult();
            if (batches.Count == 1) await release.Task;
            Interlocked.Decrement(ref active);
            return commit.Document;
        });
        var local = AcceptName(saves, CreateDocument("prefix"), "first");
        var flush = saves.FlushAsync(local.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        local = AcceptName(saves, local, "later");
        Assert.Equal("later", local.DisplayName);
        Assert.Equal(0, saves.GetState(local.Id)!.DurableContentRevision);
        release.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("later", local.DisplayName);
        Assert.Equal(1, maximum);
        Assert.Equal(new long[] { 1, 2 }, batches.Select(batch => batch.Intent.ExpectedRevision));
        Assert.Equal(2, saves.GetState(local.Id)!.DurableContentRevision);
        Assert.Equal(3, local.Revision);
    }

    [Fact]
    public async Task ContinuouslyArrivingActionsAreScheduledByTwoSeconds()
    {
        var first = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = Stopwatch.StartNew();
        using var saves = new SimulationDocumentSaveCoordinator((commit, _) =>
        { first.TrySetResult(elapsed.Elapsed); return Task.FromResult(commit.Document); });
        var local = CreateDocument("maximum");
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(2300))
        {
            local = AcceptName(saves, local, elapsed.ElapsedMilliseconds.ToString());
            await Task.Delay(50);
        }
        var scheduling = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(scheduling.TotalMilliseconds, 1800, 3500);
        await saves.FlushAsync(local.Id);
        Assert.True(saves.GetState(local.Id)!.IsDurable);
    }

    [Fact]
    public async Task ABlockedDocumentDoesNotBlockAnotherAndCallerCancellationDoesNotCancelIssuedCas()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var saves = new SimulationDocumentSaveCoordinator(async (commit, token) =>
        {
            Assert.False(token.CanBeCanceled);
            if (commit.Document.Id.EndsWith(":a")) { entered.TrySetResult(); await release.Task; }
            return commit.Document;
        });
        var a = AcceptName(saves, CreateDocument("a"), "A");
        using var cancellation = new CancellationTokenSource();
        var aWait = saves.FlushAsync(a.Id, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aWait);
        var b = AcceptName(saves, CreateDocument("b"), "B");
        await saves.FlushAsync(b.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(saves.GetState(b.Id)!.IsDurable);
        Assert.False(saves.GetState(a.Id)!.IsDurable);
        release.SetResult();
        await saves.FlushAsync(a.Id);
    }

    [Fact]
    public async Task FailedSavePausesUntilExplicitRetryAndReusesOriginalCommit()
    {
        var attempts = new ConcurrentQueue<SimulationEditCommit>();
        using var saves = new SimulationDocumentSaveCoordinator((commit, _) =>
        {
            attempts.Enqueue(commit);
            return attempts.Count == 1 ? Task.FromException<StorageSystemDocument>(new SimulationCommitOutcomeUnknownException(commit.Intent.CommitId))
                : Task.FromResult(commit.Document);
        });
        var local = AcceptName(saves, CreateDocument("retry"), "preserved");
        await Assert.ThrowsAsync<IOException>(() => saves.FlushAsync(local.Id));
        Assert.Equal(SimulationSavePhase.OutcomeUnknown, saves.GetState(local.Id)!.Phase);
        await Task.Delay(400);
        Assert.Single(attempts);
        Assert.Equal("preserved", local.DisplayName);
        await saves.RetryAsync(local.Id);
        Assert.Equal(2, attempts.Count);
        Assert.Same(attempts.First(), attempts.Last());
        Assert.True(saves.GetState(local.Id)!.IsDurable);
    }

    [Fact]
    public async Task ConflictRetainsDraftAndCancellingLifecycleLeavesSavingAvailable()
    {
        var conflict = true;
        using var saves = new SimulationDocumentSaveCoordinator((commit, _) => conflict
            ? Task.FromException<StorageSystemDocument>(new SimulationCommitConflictException()) : Task.FromResult(commit.Document));
        var local = AcceptName(saves, CreateDocument("conflict"), "human draft");
        saves.SetAccepting(false);
        await Assert.ThrowsAsync<IOException>(() => saves.FlushAllAsync());
        Assert.Equal(SimulationSavePhase.Conflict, saves.GetState(local.Id)!.Phase);
        await Assert.ThrowsAsync<SimulationCommitConflictException>(() => saves.RetryAsync(local.Id));
        saves.SetAccepting(true);
        var independent = AcceptName(saves, CreateDocument("independent"), "still editable");
        conflict = false;
        await saves.FlushAsync(independent.Id);
        Assert.Equal("human draft", local.DisplayName);
        Assert.True(saves.GetState(independent.Id)!.IsDurable);
    }

    [Fact]
    public async Task DeleteWaitsForIssuedBatchAndLateCompletionCannotResurrectDiscardedGeneration()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var saves = new SimulationDocumentSaveCoordinator(async (commit, _) =>
        { entered.TrySetResult(); await release.Task; return commit.Document; });
        var local = AcceptName(saves, CreateDocument("delete"), "first");
        var flush = saves.FlushAsync(local.Id);
        await entered.Task;
        local = AcceptName(saves, local, "never submitted");
        var deletion = saves.PrepareDeleteAsync(local.Id);
        Assert.False(deletion.IsCompleted);
        release.SetResult();
        Assert.Equal(2, await deletion.WaitAsync(TimeSpan.FromSeconds(5)));
        saves.Discard(local.Id);
        await flush;
        Assert.Equal(SimulationSavePhase.Deleted, saves.GetState(local.Id)!.Phase);
        Assert.Throws<InvalidOperationException>(() => AcceptName(saves, local, "must not revive"));
    }

    [Fact]
    public async Task CancelledDeleteRestoresNeverIssuedPendingEdits()
    {
        var submitted = new ConcurrentQueue<SimulationEditCommit>();
        using var saves = new SimulationDocumentSaveCoordinator((commit, _) =>
        { submitted.Enqueue(commit); return Task.FromResult(commit.Document); });
        var local = AcceptName(saves, CreateDocument("delete-cancel"), "kept draft");
        Assert.Equal(1, await saves.PrepareDeleteAsync(local.Id));
        Assert.Empty(submitted);
        saves.CancelDelete(local.Id);
        await saves.FlushAsync(local.Id);
        Assert.Equal("kept draft", Assert.Single(submitted).Document.DisplayName);
        Assert.True(saves.GetState(local.Id)!.IsDurable);
    }

    [Fact]
    public void BatchedWorkModelMatchesSequentialFrozenRulesAndLogicalFactsRevision()
    {
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "simulation:equivalence",
            StorageSystemKind.Simulation, "test", SimulationLayouts.PoolWithoutVirtualDisk(), [], DateTimeOffset.UnixEpoch);
        var pool = document.Snapshot.StoragePools.First(item => !item.IsPrimordial);
        var edits = SimulationEditIntent.Freeze([
            new(SimulationEditKind.Rename, pool.StableId, Name: "renamed"),
            new(SimulationEditKind.CreateVirtualDisk, pool.StableId, Name: "space", SizeBytes: 8L << 30,
                CreatePartition: true, CreateMsr: true, FileSystem: "NTFS", AllocationUnitSize: 65536),
            new(SimulationEditKind.OptimizePool, pool.StableId)
        ], "same-frozen-plan", DateTimeOffset.UnixEpoch.AddDays(1));
        var service = new SimulationOperationService();
        var sequential = document;
        foreach (var edit in edits)
        {
            var next = service.Reduce(sequential, edit);
            Assert.True(next.Succeeded, next.Error); sequential = next.Document;
        }
        var batch = service.ReducePlan(document, new("batch", edits));
        Assert.True(batch.Succeeded, batch.Error);
        Assert.Equal(sequential.SourceFacts!.Revision, batch.Document.SourceFacts!.Revision);
        Assert.Equal(SimulationDocumentCodec.Encode(sequential).Sha256, SimulationDocumentCodec.Encode(batch.Document).Sha256);
    }

    private static StorageSystemDocument AcceptName(SimulationDocumentSaveCoordinator saves, StorageSystemDocument document, string name)
    {
        var edit = SimulationEditIntent.Freeze(new SimulationEditRequest(SimulationEditKind.Rename, document.Snapshot.Computer.StableId, Name: name),
            Guid.NewGuid().ToString("N"), DateTimeOffset.UnixEpoch);
        return saves.Accept(new(document with { DisplayName = name, Revision = document.Revision + 1 },
            new(document.Id, document.Revision, Guid.NewGuid().ToString("N"), [edit])));
    }

    private static StorageSystemDocument CreateDocument(string name) => new(StorageSystemDocument.CurrentSchemaVersion,
        $"simulation:{name}", StorageSystemKind.Simulation, name, StorageSnapshot.Empty(name), [], DateTimeOffset.UnixEpoch);
}
