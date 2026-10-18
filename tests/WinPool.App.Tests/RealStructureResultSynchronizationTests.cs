using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

/// <summary>Exercises the production frozen-stage owner and recovery ordering, not a second draft consumer.</summary>
public sealed class RealStructureResultSynchronizationTests
{
    [Fact]
    public async Task CachedFrozenRecoveryMapsTheLocalDraftAndReleasesTheExactBarrierWhileASimulationIsSelected()
    {
        var f = new Fixture();
        f.Register();
        var unknown = f.Frozen with { State = RealOperationState.OutcomeUnknown, RequiresReconciliation = true };
        Assert.True(f.Submission.Observe(unknown));
        f.Results.ObserveUnresolved(unknown);
        var batch = f.Fresh with { SourceFacts = f.Fresh.SourceFacts! with { ObservationId = Guid.NewGuid() } };
        var scope = batch.SourceFacts!.ScopedCollection!.Scope;
        var catalog = new StorageSystemCatalog();
        catalog.ReplaceLocal(f.Document);
        var selected = batch.AsImportedSimulation("Selected simulation");
        catalog.AddSimulation(selected);
        var connection = new RecordingConnection(_ => throw new InvalidOperationException("Recovery must not issue a write or load historical evidence."));
        var cache = RealOperationStatusCache.For(connection);
        cache.Remember(unknown);
        var terminal = Assert.IsType<AgentRealOperationResponse>(await cache.ResolveAsync(LightStatus(f.Terminal, 2), default));
        var notified = false;

        Assert.True(await f.Results.RecoverAsync(terminal, () =>
        {
            Assert.True(f.Submission.IsBlocked);
            Assert.True(f.Session.OutcomeUnknown);
            Assert.True(catalog.TryReplaceLocalReport(batch));
            Assert.False(RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(selected, batch, scope));
            return Task.FromResult(RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(catalog, batch, scope));
        }, () => catalog.Systems.Single(document => document.IsLocal), (_, refreshed) =>
        {
            notified = true;
            Assert.True(refreshed);
            Assert.False(f.Session.OutcomeUnknown);
            Assert.False(f.Session.RealResultSynchronizationPending);
            Assert.Equal("vd:verified", f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
            Assert.True(f.Submission.IsBlocked); // Draft mapping and notification precede release.
            return true;
        }, status => Assert.True(f.Submission.Observe(status)), default));

        Assert.True(notified);
        Assert.False(f.Submission.IsBlocked);
        Assert.False(f.Session.OutcomeUnknown);
        Assert.False(f.Session.RealResultSynchronizationPending);
        Assert.Same(selected, catalog.Find(selected.Id));
        Assert.False(selected.IsLocal);
        Assert.Empty(connection.Requests);
    }

    [Theory]
    [InlineData(RealOperationState.Running)]
    [InlineData(RealOperationState.OutcomeUnknown)]
    public async Task RecoveredOperationFollowsTerminalEventAndMapsFactsBeforeReleasingBarrier(RealOperationState initialState)
    {
        var f = new Fixture();
        f.Register();
        var operation = f.Frozen with { State = initialState, RequiresReconciliation = initialState == RealOperationState.OutcomeUnknown };
        long revision = 1;
        AgentRealOperationStatusResponse Status() => LightStatus(operation, revision);
        var connection = new RecordingConnection(request => Success(request switch
        {
            QueryAgentRealOperationRequest => Status(),
            ListAgentRecoverableRealOperationsRequest => new AgentRecoverableRealOperationsResponse([Status()]),
            LoadAgentRealOperationEvidenceRequest => new AgentRealOperationEvidenceResponse(operation, 0, null),
            _ => throw new InvalidOperationException("Recovery must not submit another write.")
        }, request));
        RealOperationStatusCache.For(connection).Remember(f.Frozen);
        await RealOperationRecovery.RefreshAsync(connection, f.Submission, ObserveUnfinished, CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;
        var notifications = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var following = FollowAsync();
        await started.Task.WaitAsync(timeout.Token);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.Equal(initialState == RealOperationState.OutcomeUnknown, f.Session.OutcomeUnknown);

        // Neither another operation nor an older terminal revision can close this identity.
        await connection.Events.Writer.WriteAsync(new AgentRealOperationStatusEvent(
            Status() with { OperationId = OperationId.New(), State = RealOperationState.Succeeded }, DateTimeOffset.UtcNow));
        await connection.Events.Writer.WriteAsync(new AgentRealOperationStatusEvent(
            Status() with { Revision = 0, State = RealOperationState.Succeeded, RequiresReconciliation = false }, DateTimeOffset.UtcNow));
        operation = f.Terminal;
        revision = 2;
        var terminal = new AgentRealOperationStatusEvent(Status(), DateTimeOffset.UtcNow);
        await connection.Events.Writer.WriteAsync(terminal);
        await refreshing.Task.WaitAsync(timeout.Token);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(f.Session.RealResultSynchronizationPending);
        await connection.Events.Writer.WriteAsync(terminal);
        await connection.Events.Writer.WriteAsync(terminal with { Status = terminal.Status with { Revision = 1, State = RealOperationState.Running } });
        allowRefresh.SetResult(true);
        await following.WaitAsync(timeout.Token);

        Assert.False(f.Submission.IsBlocked);
        Assert.False(f.Session.RealResultSynchronizationPending);
        Assert.False(f.Session.OutcomeUnknown);
        Assert.Equal("vd:verified", f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
        Assert.Equal(f.Fresh.Revision, f.Session.BaselineRevision);
        Assert.Equal(1, refreshCount);
        Assert.Equal(1, notifications);
        Assert.Equal(0, connection.ActiveWatchers);
        Assert.Equal(1, connection.WatchCalls);
        Assert.Empty(connection.Requests.OfType<LoadAgentRealOperationEvidenceRequest>());
        Assert.All(connection.Requests, request => Assert.True(request is QueryAgentRealOperationRequest
            or ListAgentRecoverableRealOperationsRequest or LoadAgentRealOperationEvidenceRequest));

        void ObserveUnfinished(AgentRealOperationResponse status)
        {
            f.Results.ObserveUnresolved(status);
            Assert.True(f.Submission.Observe(status));
        }
        async Task FollowAsync()
        {
            await foreach (var status in RealOperationRecovery.WatchAsync(connection, f.Submission, timeout.Token))
            {
                if (!RealOperationSubmissionState.IsReconciledTerminal(status))
                {
                    ObserveUnfinished(status);
                    started.TrySetResult();
                    continue;
                }
                Assert.True(await f.Results.RecoverAsync(status, async () =>
                {
                    refreshCount++;
                    refreshing.TrySetResult();
                    return await allowRefresh.Task.WaitAsync(timeout.Token);
                }, () => f.Fresh, (_, _) =>
                {
                    notifications++;
                    Assert.True(f.Submission.IsBlocked);
                    Assert.False(f.Session.OutcomeUnknown);
                    Assert.False(f.Session.RealResultSynchronizationPending);
                    return true;
                }, value => Assert.True(f.Submission.Observe(value)), timeout.Token));
            }
        }
    }

    [Fact]
    public async Task RecoveryReseedUsesDurableRevisionAndAcceptsTerminalAfterUtcClockMovesBack()
    {
        var f = new Fixture();
        f.Register();
        var operation = f.Frozen with { State = RealOperationState.OutcomeUnknown, RequiresReconciliation = true };
        f.Submission.Observe(operation);
        f.Results.ObserveUnresolved(operation);
        long revision = 10;
        var queries = 0;
        var reseeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentRealOperationStatusResponse Status() => LightStatus(operation, revision);
        var connection = new RecordingConnection(request =>
        {
            AgentResponse response;
            if (request is QueryAgentRealOperationRequest)
            {
                response = Status();
                if (++queries == 2) reseeded.TrySetResult();
            }
            else if (request is LoadAgentRealOperationEvidenceRequest)
                response = new AgentRealOperationEvidenceResponse(operation, 0, null);
            else throw new InvalidOperationException();
            return Success(response, request);
        });
        RealOperationStatusCache.For(connection).Remember(f.Frozen);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var watcher = RealOperationRecovery.WatchAsync(connection, f.Submission, timeout.Token).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        var next = watcher.MoveNextAsync().AsTask();
        var now = DateTimeOffset.UtcNow;
        await connection.Events.Writer.WriteAsync(new AgentStateReseedEvent(
            new(new AgentInstanceId(Guid.NewGuid()), true, null, new(AgentLifecycleState.Running, null, [], [], false), []),
            "agent.events.reconnected", now.AddMinutes(1)));
        await reseeded.Task.WaitAsync(timeout.Token);
        await connection.Events.Writer.WriteAsync(new AgentRealOperationStatusEvent(
            Status() with { Revision = 9, State = RealOperationState.Succeeded, RequiresReconciliation = false }, now.AddMinutes(2)));
        operation = f.Terminal;
        revision = 11;
        var terminal = new AgentRealOperationStatusEvent(Status(), now.AddMinutes(-1));
        await connection.Events.Writer.WriteAsync(terminal);
        Assert.True(await next.WaitAsync(timeout.Token));
        Assert.Equal(RealOperationState.Succeeded, watcher.Current.State);
        Assert.True(await f.Results.RecoverAsync(watcher.Current, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, value => Assert.True(f.Submission.Observe(value)), timeout.Token));
        // Duplicate terminal and older Running events cannot reopen the closed identity.
        await connection.Events.Writer.WriteAsync(terminal);
        await connection.Events.Writer.WriteAsync(terminal with { Status = terminal.Status with { Revision = 10, State = RealOperationState.Running } });
        Assert.False(await watcher.MoveNextAsync());
        Assert.False(f.Submission.IsBlocked);
        Assert.False(f.Session.OutcomeUnknown);
        Assert.Equal(2, queries);
        Assert.Empty(connection.Requests.OfType<LoadAgentRealOperationEvidenceRequest>());
        Assert.Equal(0, connection.ActiveWatchers);
    }

    [Fact]
    public async Task CancelledRecoverySubscriptionDisposesWatcherAndKeepsUnknownBarrier()
    {
        var f = new Fixture();
        f.Register();
        var unknown = f.Frozen with { State = RealOperationState.OutcomeUnknown, RequiresReconciliation = true };
        f.Submission.Observe(unknown);
        f.Results.ObserveUnresolved(unknown);
        var connection = new RecordingConnection(request => Success(request switch
        {
            QueryAgentRealOperationRequest => new AgentRealOperationStatusResponse(unknown.Plan.OperationId, 1,
                unknown.State, [], null, true),
            LoadAgentRealOperationEvidenceRequest => new AgentRealOperationEvidenceResponse(unknown, 0, null),
            _ => throw new InvalidOperationException()
        }, request));
        RealOperationStatusCache.For(connection).Remember(f.Frozen);
        using var cancellation = new CancellationTokenSource();
        await using var watcher = RealOperationRecovery.WatchAsync(connection, f.Submission, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        var pending = watcher.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, connection.ActiveWatchers);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(f.Session.OutcomeUnknown);
        Assert.True(f.Session.RealResultSynchronizationPending);
    }

    [Theory]
    [InlineData("accept-reply-lost")]
    [InlineData("no-query-reply")]
    [InlineData("running-then-disconnected")]
    [InlineData("polling-exhausted")]
    public async Task AcceptedInterruptionRecoversTheSameVdAndRemainingGoalWithoutAnotherAccept(string interruption)
    {
        var f = new Fixture();
        var queries = 0;
        var recovering = false;
        var connection = new RecordingConnection(request => request switch
        {
            PrepareAgentRealOperationRequest => Success(f.Frozen, request),
            AcceptAgentRealOperationRequest when interruption == "accept-reply-lost" => throw new IOException("accept reply lost"),
            AcceptAgentRealOperationRequest => Success(f.Frozen with { State = RealOperationState.Accepted }, request),
            QueryAgentRealOperationRequest when recovering => Success(f.Terminal, request),
            QueryAgentRealOperationRequest when interruption == "running-then-disconnected" && queries++ == 0 =>
                Success(f.Frozen with { State = RealOperationState.Running }, request),
            QueryAgentRealOperationRequest when interruption == "polling-exhausted" =>
                Success(f.Frozen with { State = RealOperationState.Running }, request),
            QueryAgentRealOperationRequest => throw new IOException("query reply lost"),
            ListAgentRecoverableRealOperationsRequest => Success(new AgentRecoverableRealOperationsResponse([]), request),
            _ => throw new InvalidOperationException()
        });
        var requestSession = new RealOperationRequestSession(connection, "product-session");
        var preparation = await requestSession.PrepareAsync(f.Proposal, CancellationToken.None);
        var frozen = Assert.IsType<AgentRealOperationResponse>(preparation.Value);
        Assert.True(f.Submission.TryReservePreparation());
        Assert.True(f.Submission.TrackPrepared(frozen));
        requestSession.Confirm(frozen);
        f.Results.Register(f.Session, frozen, f.Proposal, f.Mapping);
        if (interruption == "accept-reply-lost")
            await Assert.ThrowsAsync<IOException>(() => requestSession.AcceptOnceAsync(CancellationToken.None));
        else
            await requestSession.AcceptOnceAsync(CancellationToken.None);
        if (interruption is "no-query-reply" or "accept-reply-lost")
            await Assert.ThrowsAsync<IOException>(() => requestSession.QueryAsync(CancellationToken.None));
        else if (interruption == "running-then-disconnected")
        {
            var running = Assert.IsType<AgentRealOperationResponse>((await requestSession.QueryAsync(CancellationToken.None)).Value);
            Assert.True(f.Submission.Observe(running));
            await Assert.ThrowsAsync<IOException>(() => requestSession.QueryAsync(CancellationToken.None));
        }
        else
        {
            // The poll budget is caller policy. Any number of Running replies leaves
            // the production stage unchanged when that caller stops waiting.
            for (var attempt = 0; attempt < 3; attempt++)
                Assert.True(f.Submission.Observe(Assert.IsType<AgentRealOperationResponse>(
                    (await requestSession.QueryAsync(CancellationToken.None)).Value)));
        }
        Assert.True(f.Submission.IsBlocked);
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.False(f.Session.OutcomeUnknown); // A transport failure is not an Agent OutcomeUnknown.
        recovering = true;
        AgentRealOperationResponse? terminal = null;
        await RealOperationRecovery.RefreshAsync(connection, f.Submission, status => terminal = status, CancellationToken.None);
        Assert.NotNull(terminal); // Exact query finds it even though unfinished list is empty.
        var notified = false;
        Assert.True(await f.Results.RecoverAsync(terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) =>
            {
                notified = true;
                Assert.True(f.Submission.IsBlocked); // Mapping precedes release.
                Assert.False(f.Session.RealResultSynchronizationPending);
                Assert.Equal("vd:verified", f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
                Assert.DoesNotContain(f.Session.Working.VirtualDisks, vd => EditWorkspace.IsDraftVirtualDisk(vd.StableId));
                return true;
            }, status => Assert.True(f.Submission.Observe(status)), CancellationToken.None));
        Assert.True(notified);
        Assert.False(f.Submission.IsBlocked);
        Assert.False(f.Session.HasBaselineConflict);
        Assert.True(f.Session.PoolIntents[Fixture.PoolId].PendingAutomaticLayout);
        Assert.Equal("A", f.Session.PoolIntents[Fixture.PoolId].VolumeName);
        Assert.Equal(f.Fresh.Revision, f.Session.BaselineRevision);
        Assert.Empty(f.Session.UndoStack);
        Assert.Single(connection.Requests.OfType<PrepareAgentRealOperationRequest>());
        Assert.Single(connection.Requests.OfType<AcceptAgentRealOperationRequest>());
        Assert.All(connection.Requests.OfType<QueryAgentRealOperationRequest>(), query => Assert.Equal(f.Frozen.Plan.OperationId, query.OperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => requestSession.AcceptOnceAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("refresh-failed")]
    [InlineData("incomplete-batch")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-system")]
    [InlineData("missing-output")]
    [InlineData("unreliable-output")]
    [InlineData("wrong-parent")]
    [InlineData("mismatched-hash")]
    public async Task FailedOrUnrelatedRecoveryKeepsBothGatesAndRetriesTheOriginalStage(string defect)
    {
        var f = new Fixture();
        f.Register();
        var fresh = f.Fresh;
        var status = f.Terminal;
        var facts = fresh.SourceFacts!;
        fresh = defect switch
        {
            "incomplete-batch" => fresh with { SourceFacts = facts with { ScopedCollection = facts.ScopedCollection! with { Complete = false } } },
            "wrong-operation" => fresh with { SourceFacts = facts with { ScopedCollection = facts.ScopedCollection! with
                { Scope = facts.ScopedCollection!.Scope with { OperationId = OperationId.New() } } } },
            "wrong-system" => fresh with { SystemId = SystemId.New() },
            "missing-output" => f.Project(fresh.Snapshot with { VirtualDisks = [], OsDisks = [] }, true),
            "unreliable-output" => fresh with { SourceFacts = facts with { Objects = facts.Objects.Select(item =>
                item.Id == "vd:verified" ? item with { HasReliableIdentity = false } : item).ToImmutableArray() } },
            "wrong-parent" => f.Project(fresh.Snapshot with
            {
                StoragePools = fresh.Snapshot.StoragePools.Append(fresh.Snapshot.StoragePools.Single() with
                    { StableId = "other-pool", MemberPhysicalDiskIds = [] }).ToArray(),
                VirtualDisks = fresh.Snapshot.VirtualDisks.Select(vd => vd with { PoolStableId = "other-pool" }).ToArray()
            }, true),
            _ => fresh
        };
        if (defect == "mismatched-hash") status = status with { Plan = status.Plan with { PlanHash = "other-hash" } };
        var releases = 0;
        try
        {
            Assert.False(await f.Results.RecoverAsync(status, () => Task.FromResult(defect != "refresh-failed"), () => fresh,
                (_, _) => true, _ => releases++, CancellationToken.None));
        }
        catch (InvalidDataException) when (defect is "missing-output" or "unreliable-output" or "wrong-parent") { }
        Assert.Equal(0, releases);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.Null(f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
        Assert.True(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, response => Assert.True(f.Submission.Observe(response)), CancellationToken.None));
        Assert.False(f.Submission.IsBlocked);
        Assert.False(f.Session.RealResultSynchronizationPending);
    }

    [Fact]
    public async Task SuccessfulReceiptWithFailedRefreshFreezesEditsAndFieldMergePreservesNewerParameters()
    {
        var f = new Fixture();
        f.Register();
        var binding = f.Session.BindingGeneration;
        var content = f.Session.ContentRevision;
        Assert.False(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(false), () => f.Document,
            (_, _) => true, _ => throw new InvalidOperationException("must not release"), CancellationToken.None));
        Assert.True(f.Session.RealResultSynchronizationPending); // Page CanEditDraft uses this production gate.
        // Defense in depth for non-UI callers/history: same binding does not imply same content.
        var changed = f.Session.Capture() with { PoolIntents = new Dictionary<string, PoolEditIntent>
            { [Fixture.PoolId] = f.Session.PoolIntents[Fixture.PoolId] with { VolumeName = "B", DriveLetter = 'F' } } };
        f.Session.Restore(changed);
        Assert.Equal(binding, f.Session.BindingGeneration);
        Assert.True(f.Session.ContentRevision > content);
        Assert.True(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, response => Assert.True(f.Submission.Observe(response)), CancellationToken.None));
        var intent = f.Session.PoolIntents[Fixture.PoolId];
        Assert.Equal("B", intent.VolumeName);
        Assert.Equal('F', intent.DriveLetter);
        Assert.Equal("vd:verified", intent.VerifiedVirtualDiskId);
        Assert.True(intent.PendingAutomaticLayout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReboundOrDiscardedDraftNeverReceivesOldMapping(bool discard)
    {
        var f = new Fixture();
        f.Register();
        var binding = f.Session.BindingGeneration;
        if (discard) f.Session.Discard();
        else f.Session.Bind(f.Document, f.Document.Snapshot);
        f.Session.PoolIntents[Fixture.PoolId] = new(true, false, "NTFS", 65536, "new binding");
        var newerDraft = f.Session.Capture();
        Assert.True(f.Session.BindingGeneration > binding);
        Assert.True(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, response => Assert.True(f.Submission.Observe(response)), CancellationToken.None));
        Assert.Same(newerDraft.Snapshot, f.Session.Working);
        Assert.Equal("new binding", f.Session.PoolIntents[Fixture.PoolId].VolumeName);
        Assert.Null(f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
        Assert.False(f.Results.HasPending(f.Session));
    }

    [Theory]
    [InlineData(RealOperationState.Cancelled)]
    [InlineData(RealOperationState.Rejected)]
    public void ExplicitNoCallTerminalClearsStageWithoutPretendingThatCreationSucceeded(RealOperationState terminal)
    {
        var f = new Fixture();
        f.Register();
        Assert.False(f.Results.Reconcile(f.Frozen with { State = terminal }, f.Document, false)); // Empty progress is not no-call proof.
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.True(f.Results.Reconcile(f.Frozen with { State = terminal,
            Steps = [new("create-vdisk", RealOperationStepState.StoppedBeforeCall, null, null, null)] }, f.Document, false));
        Assert.False(f.Session.RealResultSynchronizationPending);
        Assert.Null(f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
        Assert.Contains(f.Session.Working.VirtualDisks, vd => EditWorkspace.IsDraftVirtualDisk(vd.StableId));
    }

    [Theory]
    [InlineData(RealOperationState.Cancelled, true)]
    [InlineData(RealOperationState.Cancelled, false)]
    [InlineData(RealOperationState.Rejected, true)]
    [InlineData(RealOperationState.Rejected, false)]
    public async Task BackgroundNoCallTerminalRebasesBeforePageNotificationAndRetainsStageWhenRefreshFails(
        RealOperationState terminalState, bool firstRefreshSucceeds)
    {
        var f = new Fixture();
        f.Register();
        var terminal = f.Frozen with { State = terminalState,
            Steps = [new("create-vdisk", RealOperationStepState.StoppedBeforeCall, null, null, null)] };
        var fresh = f.Project(f.Document.Snapshot, true); // Fresh facts prove the VD was not created.
        var oldRevision = f.Session.BaselineRevision;
        var oldSnapshotVersion = f.Session.Baseline.SnapshotVersion;
        Assert.NotEqual(oldRevision, fresh.Revision);
        Assert.NotEqual(oldSnapshotVersion, fresh.Snapshot.SnapshotVersion);
        var notifications = 0;
        var releases = 0;
        if (!firstRefreshSucceeds)
        {
            Assert.False(await f.Results.RecoverAsync(terminal, () => Task.FromResult(false), () => fresh,
                NotifyPage, Release, CancellationToken.None));
            Assert.True(f.Results.Owns(terminal));
            Assert.True(f.Results.HasPending(f.Session));
            Assert.True(f.Session.RealResultSynchronizationPending);
            Assert.True(f.Submission.IsBlocked);
            Assert.Equal(oldRevision, f.Session.BaselineRevision);
            Assert.Equal(oldSnapshotVersion, f.Session.Baseline.SnapshotVersion);
            Assert.Equal(0, notifications);
            Assert.Equal(0, releases);
        }
        Assert.True(await f.Results.RecoverAsync(terminal, () => Task.FromResult(true), () => fresh,
            NotifyPage, Release, CancellationToken.None));
        Assert.Equal(1, notifications);
        Assert.Equal(1, releases);
        Assert.False(f.Results.Owns(terminal));
        Assert.False(f.Session.RealResultSynchronizationPending);
        Assert.False(f.Submission.IsBlocked);
        Assert.False(f.Session.HasBaselineConflict);
        Assert.Null(f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
        Assert.Equal("A", f.Session.PoolIntents[Fixture.PoolId].VolumeName);
        Assert.Contains(f.Session.Working.VirtualDisks, vd => EditWorkspace.IsDraftVirtualDisk(vd.StableId));
        Assert.Empty(f.Session.Baseline.VirtualDisks);

        bool NotifyPage(AgentRealOperationResponse _, bool inventoryRefreshed)
        {
            notifications++;
            Assert.True(inventoryRefreshed);
            Assert.True(f.Submission.IsBlocked);
            Assert.False(f.Session.RealResultSynchronizationPending);
            Assert.Equal(fresh.Revision, f.Session.BaselineRevision);
            Assert.Equal(fresh.Snapshot.SnapshotVersion, f.Session.Baseline.SnapshotVersion);
            // StorageStructurePage.OnRealInventoryRefreshed calls this production
            // method when the unexecuted placeholder still makes its draft dirty.
            f.Session.MarkBaselineConflict(fresh);
            Assert.False(f.Session.HasBaselineConflict);
            return true;
        }
        void Release(AgentRealOperationResponse response)
        {
            releases++;
            Assert.Equal(1, notifications);
            Assert.True(f.Submission.Observe(response));
        }
    }

    [Fact]
    public async Task AgentUnknownStaysDistinctAndExitCancellationCannotReleaseBeforeMapping()
    {
        var f = new Fixture();
        f.Register();
        var unknown = f.Frozen with { State = RealOperationState.OutcomeUnknown, RequiresReconciliation = true,
            Steps = [new("create-vdisk", RealOperationStepState.OutcomeUnknown, null, null, null)] };
        f.Results.ObserveUnresolved(unknown);
        Assert.True(f.Session.OutcomeUnknown);
        Assert.False(f.Results.Reconcile(unknown, f.Fresh, true));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Results.RecoverAsync(f.Terminal,
            () => { cancellation.Cancel(); return Task.FromResult(true); }, () => f.Fresh,
            (_, _) => true, _ => throw new InvalidOperationException("cancelled recovery released"), cancellation.Token));
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, response => Assert.True(f.Submission.Observe(response)), CancellationToken.None));
        Assert.False(f.Session.OutcomeUnknown);
    }

    [Fact]
    public async Task BindingToAnotherSystemDoesNotTrapTheOldOperationOrChangeTheNewDraft()
    {
        var f = new Fixture();
        f.Register();
        var otherSystem = SystemId.New();
        var otherSnapshot = StorageSnapshot.Empty("Other system");
        var other = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:other-stage", StorageSystemKind.Local,
            "Other system", WinPoolSimulationFacts.Create(otherSnapshot, otherSystem) with { IsSimulation = false }, [], DateTimeOffset.UtcNow)
            { SystemId = otherSystem };
        f.Session.Bind(other, other.Snapshot);
        f.Session.PoolIntents["new-goal"] = new(true, false, "NTFS", 65536, "new system draft");
        var newer = f.Session.Capture();
        Assert.False(f.Results.Reconcile(f.Terminal, other, true));
        Assert.True(f.Submission.IsBlocked);
        Assert.True(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, status => Assert.True(f.Submission.Observe(status)), CancellationToken.None));
        Assert.Equal(otherSystem, f.Session.SystemId);
        Assert.Same(newer.Snapshot, f.Session.Working);
        Assert.Equal("new system draft", f.Session.PoolIntents["new-goal"].VolumeName);
        Assert.False(f.Session.RealResultSynchronizationPending);
        Assert.False(f.Results.Owns(f.Terminal));
    }

    [Fact]
    public async Task FailedStepAfterCallNeedsFreshFactsEvenWhenNoStepWasVerified()
    {
        var f = new Fixture();
        f.Register();
        var failed = f.Frozen with { State = RealOperationState.Failed,
            Steps = [new("create-vdisk", RealOperationStepState.Failed, "real.unexpected_effect", null, "{\"CallIssued\":true}")] };
        Assert.False(await f.Results.RecoverAsync(failed, () => Task.FromResult(false), () => f.Document,
            (_, _) => true, _ => throw new InvalidOperationException("unrefreshed failure released"), CancellationToken.None));
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(await f.Results.RecoverAsync(failed, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, status => Assert.True(f.Submission.Observe(status)), CancellationToken.None));
        Assert.Null(f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId); // Unexpected output is not adopted as a verified creation.
        Assert.False(f.Session.RealResultSynchronizationPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredAutomaticLayoutAdvancesOnlyTheVerifiedStageAndNeverReplaysItsPartition(bool changedIntent)
    {
        var f = new Fixture();
        var baseline = f.Project(f.Fresh.Snapshot with { OsDisks = f.Fresh.Snapshot.OsDisks.Select(disk => disk with { PartitionStyle = "GPT" }).ToArray() }, false);
        f.Session.Bind(baseline, baseline.Snapshot);
        var intent = new PoolEditIntent(true, true, "NTFS", 65536, "A", CreateMsr: false, DriveLetter: 'E',
            VerifiedVirtualDiskId: "vd:verified", PendingAutomaticLayout: true);
        f.Session.PoolIntents[Fixture.PoolId] = intent;
        var layout = RealOperationProposalFactory.ConfigureInitializedDisk(baseline.SystemId,
            new(baseline.SystemId, StorageObjectKind.OsDisk, "os:verified"), null, false, 32L << 30, true, "A", 'E')!;
        var progress = new RealAutomaticLayoutProgress(layout, "vd:verified", intent);
        var next = progress.BuildNext(baseline, intent)!;
        var create = Assert.IsType<CreatePartitionCommand>(Assert.Single(next.Steps).Command);
        var frozen = Freeze(next);
        Assert.True(f.Submission.TryReservePreparation());
        Assert.True(f.Submission.TrackPrepared(frozen));
        f.Results.Register(f.Session, frozen, next, layout: progress);
        var unknown = frozen with { State = RealOperationState.OutcomeUnknown, RequiresReconciliation = true,
            Steps = [new(next.Steps[0].Id, RealOperationStepState.OutcomeUnknown, null, null, null)] };
        f.Results.ObserveUnresolved(unknown);
        Assert.True(progress.RequiresReconciliation);
        var partition = new PartitionInfo("partition:recovered", true, 7, 1, "Basic", create.OffsetBytes, create.SizeBytes,
            false, false, "", "", "", null, 0, "Healthy", "OK", "", "os:verified", GptType: "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}");
        var terminal = frozen with { State = RealOperationState.Succeeded,
            Steps = [new(next.Steps[0].Id, RealOperationStepState.Verified, null, null, "{\"CreatedObjectId\":\"partition:recovered\"}")] };
        var fresh = f.Project(baseline.Snapshot with { Partitions = [partition] }, true);
        fresh = fresh with { SourceFacts = fresh.SourceFacts! with { ScopedCollection = fresh.SourceFacts!.ScopedCollection! with
            { Scope = fresh.SourceFacts!.ScopedCollection!.Scope with { OperationId = terminal.Plan.OperationId } } } };
        Assert.True(await f.Results.RecoverAsync(terminal, () => Task.FromResult(true), () => fresh,
            (_, _) => true, status => Assert.True(f.Submission.Observe(status)), CancellationToken.None));
        Assert.False(progress.RequiresReconciliation);
        Assert.False(f.Session.OutcomeUnknown);
        Assert.Single(f.Session.Working.Partitions);
        Assert.True(f.Session.PoolIntents[Fixture.PoolId].PendingAutomaticLayout);
        if (changedIntent)
            Assert.Throws<InvalidOperationException>(() => progress.BuildNext(fresh, intent with { VolumeName = "B" }));
        else
            Assert.IsType<FormatVolumeCommand>(Assert.Single(progress.BuildNext(fresh, intent)!.Steps).Command);
        // Recovery itself never calls BuildNext/Accept or starts formatting.
        Assert.False(f.Submission.IsBlocked);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("missing-os-output")]
    [InlineData("wrong-os-output")]
    public async Task InvalidVerifiedReceiptCannotPartiallyMapOrReleaseTheDraft(string defect)
    {
        var f = new Fixture();
        f.Register();
        var evidence = defect switch
        {
            "invalid-json" => "[",
            "missing-os-output" => "{\"CreatedObjectId\":\"vd:verified\"}",
            _ => "{\"CreatedObjectId\":\"vd:verified\",\"CreatedOsDiskId\":\"os:unrelated\"}"
        };
        var invalid = f.Terminal with { Steps = [f.Terminal.Steps[0] with { ResultEvidence = evidence }] };
        var failure = await Record.ExceptionAsync(() => f.Results.RecoverAsync(invalid,
            () => Task.FromResult(true), () => f.Fresh, (_, _) => true,
            _ => throw new InvalidOperationException("invalid receipt released"), CancellationToken.None));
        if (defect == "invalid-json") Assert.IsAssignableFrom<JsonException>(failure);
        else Assert.IsType<InvalidDataException>(failure);
        Assert.True(f.Submission.IsBlocked);
        Assert.True(f.Session.RealResultSynchronizationPending);
        Assert.Null(f.Session.PoolIntents[Fixture.PoolId].VerifiedVirtualDiskId);
        Assert.Contains(f.Session.Working.VirtualDisks, vd => EditWorkspace.IsDraftVirtualDisk(vd.StableId));
        Assert.True(await f.Results.RecoverAsync(f.Terminal, () => Task.FromResult(true), () => f.Fresh,
            (_, _) => true, status => Assert.True(f.Submission.Observe(status)), CancellationToken.None));
    }

    [Fact]
    public async Task PoolOnlyRecoveryMapsTheVerifiedPoolAndKeepsAutoVdOffWithoutCreatingAnotherStage()
    {
        var f = new Fixture();
        var physical = f.Document.Snapshot.PhysicalDisks.Single() with { PoolStableId = null };
        var baseline = f.Project(f.Document.Snapshot with { StoragePools = [], PhysicalDisks = [physical] }, false);
        f.Session.Bind(baseline, baseline.Snapshot);
        var draftPool = new StoragePoolInfo("draft:pool:pool-only", false, "Pool only", false, "Healthy", "OK", physical.Size, 0, null, [physical.StableId]);
        f.Session.Working = f.Session.Working with { StoragePools = [draftPool], PhysicalDisks = [physical with { PoolStableId = draftPool.StableId }] };
        f.Session.PoolIntents[draftPool.StableId] = new(false, false, "NTFS", 65536, "retained future label");
        var proposal = RealOperationProposalFactory.CreatePoolWithMembers(baseline.SystemId,
            [new(baseline.SystemId, StorageObjectKind.PhysicalDisk, physical.StableId)], draftPool.FriendlyName);
        var frozen = Freeze(proposal);
        Assert.True(f.Submission.TryReservePreparation());
        Assert.True(f.Submission.TrackPrepared(frozen));
        f.Results.Register(f.Session, frozen, proposal, new(StorageObjectKind.StoragePool, draftPool.StableId, "create-pool"));
        var terminal = frozen with { State = RealOperationState.Succeeded,
            Steps = [new("create-pool", RealOperationStepState.Verified, null, null, "{\"CreatedObjectId\":\"pool:verified-only\"}")] };
        var fresh = f.Project(baseline.Snapshot with
        {
            StoragePools = [draftPool with { StableId = "pool:verified-only", IsStable = true }],
            PhysicalDisks = [physical with { PoolStableId = "pool:verified-only" }]
        }, true);
        fresh = fresh with { SourceFacts = fresh.SourceFacts! with { ScopedCollection = fresh.SourceFacts!.ScopedCollection! with
            { Scope = fresh.SourceFacts!.ScopedCollection!.Scope with { OperationId = terminal.Plan.OperationId } } } };
        Assert.True(await f.Results.RecoverAsync(terminal, () => Task.FromResult(true), () => fresh,
            (_, _) => true, status => Assert.True(f.Submission.Observe(status)), CancellationToken.None));
        Assert.False(f.Session.PoolIntents.ContainsKey(draftPool.StableId));
        var intent = f.Session.PoolIntents["pool:verified-only"];
        Assert.False(intent.AutoCreateVirtualDisk);
        Assert.False(intent.PendingAutomaticLayout);
        Assert.Equal("retained future label", intent.VolumeName);
        Assert.Equal("pool:verified-only", Assert.Single(f.Session.Working.StoragePools).StableId);
        Assert.Equal("pool:verified-only", Assert.Single(f.Session.Working.PhysicalDisks).PoolStableId);
        Assert.Empty(f.Session.Working.VirtualDisks);
        Assert.False(f.Session.RealResultSynchronizationPending);
        Assert.False(f.Submission.IsBlocked);
    }

    [Fact]
    public void MultiMemberPoolMappingPreservesRoleGoalsLiveHardwareAndMaximumKeys()
    {
        var f = new Fixture();
        var first = f.Document.Snapshot.PhysicalDisks.Single() with { PoolStableId = null, Usage = PhysicalDiskUsage.AutoSelect };
        var second = first with { StableId = "physical:second", SerialNumber = "SECOND", DeviceId = 8 };
        var baseline = f.Project(f.Document.Snapshot with { StoragePools = [], PhysicalDisks = [first, second] }, false);
        f.Session.Bind(baseline, baseline.Snapshot);
        const string temporary = "draft:pool:multi";
        const string created = "pool:verified-multi";
        var pool = new StoragePoolInfo(temporary, false, "Multi", false, "Healthy", "OK", first.Size + second.Size, 0, null, [first.StableId, second.StableId]);
        f.Session.Working = f.Session.Working with { StoragePools = [pool], PhysicalDisks = [first with { PoolStableId = temporary },
            second with { PoolStableId = temporary, Usage = PhysicalDiskUsage.HotSpare }] };
        var layout = new StorageLayoutParameters("Simple", 1, 16384);
        var tiers = new[] { new PoolTierEditIntent("SSD", layout, 0, true) };
        var usages = new[] { new PoolMemberEditIntent(first.StableId, PhysicalDiskUsage.AutoSelect), new PoolMemberEditIntent(second.StableId, PhysicalDiskUsage.HotSpare) };
        f.Session.PoolIntents[temporary] = new(true, true, "NTFS", 65536, "Requested", Tiers: tiers, MemberUsages: usages);
        f.Session.MaximumSizeFields.UnionWith([temporary + "|SSD", temporary + "-other|HDD"]);
        var proposal = RealOperationProposalFactory.CreatePoolWithMembers(baseline.SystemId,
            [new(baseline.SystemId, StorageObjectKind.PhysicalDisk, first.StableId), new(baseline.SystemId, StorageObjectKind.PhysicalDisk, second.StableId)], "Multi");
        var frozen = Freeze(proposal);
        f.Results.Register(f.Session, frozen, proposal, new(StorageObjectKind.StoragePool, temporary, "create-pool"));
        var terminal = frozen with { State = RealOperationState.Succeeded,
            Steps = [new("create-pool", RealOperationStepState.Verified, null, null, JsonSerializer.Serialize(new { CreatedObjectId = created }))] };
        var fresh = ForOperation(f.Project(baseline.Snapshot with { StoragePools = [pool with { StableId = created, IsStable = true }],
            PhysicalDisks = [first with { PoolStableId = created, CanPool = false }, second with { PoolStableId = created, Model = "Live model", CanPool = false }] }, true), terminal);
        Assert.True(f.Results.Reconcile(terminal, fresh, true));
        var intent = f.Session.PoolIntents[created];
        Assert.Equal(tiers, intent.Tiers);
        Assert.Equal(usages, intent.MemberUsages);
        Assert.Equal("Live model", f.Session.Working.PhysicalDisks.Single(item => item.StableId == second.StableId).Model);
        Assert.False(f.Session.Working.PhysicalDisks.Single(item => item.StableId == second.StableId).CanPool);
        Assert.Equal(PhysicalDiskUsage.HotSpare, f.Session.Working.PhysicalDisks.Single(item => item.StableId == second.StableId).Usage);
        Assert.False(PhysicalDiskUsage.IsHotSpare(f.Session.Baseline.PhysicalDisks.Single(item => item.StableId == second.StableId).Usage));
        Assert.Contains(created + "|SSD", f.Session.MaximumSizeFields);
        Assert.Contains(temporary + "-other|HDD", f.Session.MaximumSizeFields);
        Assert.DoesNotContain(temporary + "|SSD", f.Session.MaximumSizeFields);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong-media")]
    [InlineData("wrong-parent")]
    [InlineData("unreliable")]
    public void TemplateMappingValidatesIdentityParentMediaAndMergesOnlyVerifiedIdentity(string defect)
    {
        var f = new Fixture();
        var physical = f.Document.Snapshot.PhysicalDisks.Single();
        var layout = new StorageLayoutParameters("Simple", 1, 16384);
        f.Session.PoolIntents[Fixture.PoolId] = f.Session.PoolIntents[Fixture.PoolId] with
        { Tiers = [new("SSD", layout, 0, true), new("HDD", new("Parity", 3, 32768, 1, 1), 32L << 30, false)] };
        var proposal = RealOperationProposalFactory.CreateTierTemplate(f.Document.SystemId,
            new(f.Document.SystemId, StorageObjectKind.StoragePool, Fixture.PoolId),
            [new(f.Document.SystemId, StorageObjectKind.PhysicalDisk, physical.StableId)], "Performance", "SSD", layout);
        var frozen = Freeze(proposal);
        f.Results.Register(f.Session, frozen, proposal, new(StorageObjectKind.StorageTier, Fixture.PoolId, "create-tier", "SSD"));
        f.Session.PoolIntents[Fixture.PoolId] = f.Session.PoolIntents[Fixture.PoolId] with { VolumeName = "Newer label" };
        var terminal = frozen with { State = RealOperationState.Succeeded,
            Steps = [new("create-tier", RealOperationStepState.Verified, null, null, "{\"CreatedObjectId\":\"tier:verified\"}")] };
        var tier = new StorageTierInfo("tier:verified", true, "Performance", defect == "wrong-media" ? "HDD" : "SSD", "Simple", 0, 0,
            defect == "wrong-parent" ? "pool:other" : Fixture.PoolId, null, [physical.StableId], 1, 16384);
        var snapshot = f.Document.Snapshot with { StorageTiers = [tier] };
        if (defect == "wrong-parent") snapshot = snapshot with { StoragePools = [..snapshot.StoragePools,
            snapshot.StoragePools[0] with { StableId = "pool:other", MemberPhysicalDiskIds = [] }] };
        var fresh = ForOperation(f.Project(snapshot, true), terminal);
        if (defect == "unreliable") fresh = fresh with { SourceFacts = fresh.SourceFacts! with { Objects = fresh.SourceFacts!.Objects.Select(item =>
            item.Id == tier.StableId ? item with { HasReliableIdentity = false } : item).ToImmutableArray() } };
        if (defect != "none")
        {
            Assert.Throws<InvalidDataException>(() => f.Results.Reconcile(terminal, fresh, true));
            Assert.Null(f.Session.PoolIntents[Fixture.PoolId].Tiers![0].VerifiedTemplateId);
            Assert.True(f.Session.RealResultSynchronizationPending);
        }
        else
        {
            Assert.True(f.Results.Reconcile(terminal, fresh, true));
            var intent = f.Session.PoolIntents[Fixture.PoolId];
            Assert.Equal("tier:verified", intent.Tiers![0].VerifiedTemplateId);
            Assert.Equal(layout, intent.Tiers[0].Layout);
            Assert.True(intent.Tiers[0].UseMaximumSize);
            Assert.Null(intent.Tiers[1].VerifiedTemplateId);
            Assert.Equal(32L << 30, intent.Tiers[1].SizeBytes);
            Assert.Equal("Newer label", intent.VolumeName);
        }
    }

    [Fact]
    public void PoolMappingRejectsPartialPhysicalSetBeforeChangingTheDraft()
    {
        var f = new Fixture();
        var physical = f.Document.Snapshot.PhysicalDisks.Single();
        var proposal = RealOperationProposalFactory.CreatePoolWithMembers(f.Document.SystemId,
            [new(f.Document.SystemId, StorageObjectKind.PhysicalDisk, physical.StableId), new(f.Document.SystemId, StorageObjectKind.PhysicalDisk, "physical:second")], "Multi");
        var frozen = Freeze(proposal);
        f.Results.Register(f.Session, frozen, proposal, new(StorageObjectKind.StoragePool, Fixture.PoolId, "create-pool"));
        var terminal = frozen with { State = RealOperationState.Succeeded,
            Steps = [new("create-pool", RealOperationStepState.Verified, null, null, "{\"CreatedObjectId\":\"pool:partial\"}")] };
        var fresh = ForOperation(f.Project(f.Document.Snapshot with
        {
            StoragePools = [f.Document.Snapshot.StoragePools[0] with { StableId = "pool:partial" }],
            PhysicalDisks = [physical with { PoolStableId = "pool:partial" }]
        }, true), terminal);
        var before = f.Session.Working;
        Assert.Throws<InvalidDataException>(() => f.Results.Reconcile(terminal, fresh, true));
        Assert.Same(before, f.Session.Working);
        Assert.True(f.Session.PoolIntents.ContainsKey(Fixture.PoolId));
        Assert.False(f.Session.PoolIntents.ContainsKey("pool:partial"));
        Assert.True(f.Session.RealResultSynchronizationPending);
    }

    private static StorageSystemDocument ForOperation(StorageSystemDocument document, AgentRealOperationResponse response) =>
        document with { SourceFacts = document.SourceFacts! with { ScopedCollection = document.SourceFacts!.ScopedCollection! with
            { Scope = document.SourceFacts.ScopedCollection.Scope with { OperationId = response.Plan.OperationId } } } };

    private static AgentRealOperationResponse Freeze(RealOperationIntentRequest proposal)
    {
        var now = DateTimeOffset.UtcNow;
        var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(),
            new(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine", ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now),
            new(SessionId.New(), "product-session", Guid.NewGuid().ToString("D"), 42, now, Path.GetFullPath("WinPool.App.exe"), true),
            "inventory", "before", "physical", "support", now, now.AddMinutes(2));
        return new(plan, RealOperationState.Prepared, [], null, false);
    }

    private static ApplicationResult<AgentResponse> Success(AgentResponse response, AgentRequest request) =>
        ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId);

    private sealed class Fixture
    {
        public const string PoolId = "pool:empty-supported";
        public SimulationEditingSession Session { get; } = new();
        public RealOperationSubmissionState Submission { get; } = new();
        public RealStructureResultSynchronization Results { get; } = new();
        public RealStructureResultSynchronization.OutputMapping Mapping { get; } = new(StorageObjectKind.VirtualDisk, PoolId, "create-vdisk");
        public StorageSystemDocument Document { get; }
        public StorageSystemDocument Fresh { get; }
        public RealOperationIntentRequest Proposal { get; }
        public AgentRealOperationResponse Frozen { get; }
        public AgentRealOperationResponse Terminal { get; }
        private readonly SystemId system = SystemId.New();
        private readonly string physicalId;

        public Fixture()
        {
            var source = SimulationLayouts.StandardTiered();
            var physical = source.PhysicalDisks.First() with { IsStable = true, PoolStableId = PoolId, IsBoot = false, IsSystem = false };
            physicalId = physical.StableId;
            var pool = new StoragePoolInfo(PoolId, true, "Existing empty pool", false, "Healthy", "OK", physical.Size, 0, null, [physicalId]);
            var baseline = source with { PhysicalDisks = [physical], StoragePools = [pool], StorageTiers = [], VirtualDisks = [], OsDisks = [], Partitions = [], Volumes = [] };
            Document = Project(baseline, false);
            Session.Bind(Document, Document.Snapshot);
            Session.Working = SimulationEditingSession.InsertRealDraftVirtualDisk(Session.Working, PoolId, "Requested VD");
            Session.PoolIntents[PoolId] = new(true, true, "NTFS", 65536, "A", VirtualDiskName: "Requested VD",
                VirtualDiskSizeBytes: 32L << 30, VirtualDiskUseMaximum: false, DriveLetter: 'E');
            Session.UndoStack.Push(Session.Capture());
            Proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(system, new(system, StorageObjectKind.StoragePool, PoolId),
                new("Requested VD", 32L << 30, false, true, true, "A", 'E', false));
            var now = DateTimeOffset.UtcNow;
            var normalized = Proposal with { Targets = Proposal.Targets.Append(new StorageObjectId(system, StorageObjectKind.PhysicalDisk, physicalId)).ToArray() };
            var plan = RealOperationPlanFactory.Create(normalized, OperationId.New(),
                new(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine", ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now),
                new(SessionId.New(), "product-session", Guid.NewGuid().ToString("D"), 42, now, Path.GetFullPath("WinPool.App.exe"), true),
                "inventory", "before", "physical", "support", now, now.AddMinutes(2));
            Frozen = new(plan, RealOperationState.Prepared, [], null, false);
            Terminal = Frozen with { State = RealOperationState.Succeeded,
                Steps = [new("create-vdisk", RealOperationStepState.Verified, null, null,
                    JsonSerializer.Serialize(new { CreatedObjectId = "vd:verified", CreatedOsDiskId = "os:verified" }))] };
            var vd = source.VirtualDisks.First() with { StableId = "vd:verified", IsStable = true, PoolStableId = PoolId, Size = 32L << 30 };
            var disk = new OsDiskInfo("os:verified", "Requested VD", 7, "RAW", vd.Size, false, false, false, null, vd.StableId);
            Fresh = Project(baseline with { VirtualDisks = [vd], OsDisks = [disk] }, true);
        }

        public StorageSystemDocument Project(StorageSnapshot snapshot, bool scoped)
        {
            var now = DateTimeOffset.UtcNow;
            var facts = WinPoolSimulationFacts.Create(snapshot, system) with { IsSimulation = false };
            if (scoped)
            {
                var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, physicalId);
                var scope = new StorageInventoryScope(system, Frozen.Plan.OperationId, null, [physical], [physicalId],
                    [new(physical, "MSFT_PhysicalDisk", "UniqueId", "physical-unique")]);
                facts = facts with { InventoryVersion = "fresh-stage", ScopedCollection = new(scope, now.AddSeconds(-1), now, true) };
            }
            return new(StorageSystemDocument.CurrentSchemaVersion, "local:stage-sync", StorageSystemKind.Local,
                "Stage sync", facts, [], now) { SystemId = system, Revision = scoped ? 2 : 1 };
        }

        public void Register()
        {
            Assert.True(Submission.TryReservePreparation());
            Assert.True(Submission.TrackPrepared(Frozen));
            Results.Register(Session, Frozen, Proposal, Mapping);
        }
    }

    private static AgentRealOperationStatusResponse LightStatus(AgentRealOperationResponse operation, long revision) =>
        new(operation.Plan.OperationId, revision, operation.State, operation.Steps.Select(step => step with
        {
            TargetEvidence = null, ResultEvidence = null,
            ResultIdentity = step.State == RealOperationStepState.Verified
                ? new("vd:verified", "os:verified") : null
        }).ToArray(), operation.Code, operation.RequiresReconciliation, operation.ReconciliationDiagnostic,
            operation.Plan.SystemId, operation.Plan.PlanHash, operation.Plan.Targets);

    private sealed class RecordingConnection(Func<AgentRequest, ApplicationResult<AgentResponse>> reply) : IAgentConnection
    {
        public List<AgentRequest> Requests { get; } = [];
        public Channel<AgentEvent> Events { get; } = Channel.CreateUnbounded<AgentEvent>();
        public int WatchCalls { get; private set; }
        public int ActiveWatchers { get; private set; }
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<AgentEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WatchCalls++;
            ActiveWatchers++;
            try
            {
                await foreach (var item in Events.Reader.ReadAllAsync(cancellationToken)) yield return item;
            }
            finally { ActiveWatchers--; }
        }
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(reply(request));
        }
    }
}
