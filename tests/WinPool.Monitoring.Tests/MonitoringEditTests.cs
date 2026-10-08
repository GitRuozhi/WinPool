using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Monitoring.Tests;

public sealed class MonitoringEditTests
{
    [Fact]
    public async Task PendingPoolStepCannotRelabelTheVirtualDiskCallOrItsPersistedGap()
    {
        var f = new Fixture(withPoolTopology: true);
        var virtualId = new StorageObjectId(f.System, StorageObjectKind.VirtualDisk, "vd");
        var request = f.Request with { Targets = f.Request.Targets.Append(new MonitorTarget(virtualId, "disk-number:4")).ToArray() };
        var source = new Source();
        var persistence = new Persistence();
        var coordinator = new MonitoringSessionCoordinator(source, persistence,
            targetIdentityResolver: new Resolver(f) { InitialTargets = request.Targets });
        Assert.True((await coordinator.StartAsync(request, default)).IsSuccess);
        var response = f.Response(new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(virtualId)));
        response = response with
        {
            Plan = response.Plan with { RealOperation = response.Plan.RealOperation! with
                { Steps = [.. response.Plan.RealOperation!.Steps,
                    new("delete-pool", new DeletePoolCommand(RealTargetReference.ForExisting(f.Pool)), ["step"], "before", "after", "", "evidence")] } },
            State = RealOperationState.Running,
            Steps = [new("step", RealOperationStepState.CallIssued, null, null, null),
                new("delete-pool", RealOperationStepState.Pending, null, null, null)]
        };
        await coordinator.ObserveAsync(response, default);
        Assert.Equal("step", coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == virtualId).StepId);
        Assert.True(source.Targets.Single(x => x.ObjectId == virtualId).SuspendedForEdit);
        Assert.All(source.Targets.Where(x => x.ObjectId != virtualId), x => Assert.False(x.SuspendedForEdit));
        Assert.Equal("delete-pool", coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).StepId);
        var missingAt = DateTimeOffset.UtcNow;
        await source.Availability!(new(request.SessionId, virtualId, "disk-number:4", false, missingAt), default);
        await WaitUntilAsync(() => persistence.Gaps.Count == 1);
        var gap = Assert.Single(persistence.Gaps.Values);
        Assert.Equal("step", gap.StepId);
        Assert.Equal(response.Plan.OperationId, gap.OperationId);
        Assert.Equal(virtualId, gap.TargetId);
        Assert.Equal(missingAt, gap.StartedAtUtc);
        Assert.Equal("monitor.edit.expected_change_sampling_paused", gap.ReasonCode);
        response = response with { Steps = [new("step", RealOperationStepState.WaitingForProvider, null, null, null),
            new("delete-pool", RealOperationStepState.Pending, null, null, null)] };
        await coordinator.ObserveAsync(response, default);
        Assert.Equal("step", coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == virtualId).StepId);
        Assert.Equal("step", Assert.Single(persistence.Gaps.Values).StepId);
        await coordinator.StopAsync(request.SessionId, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoolEditsDoNotPausePrimordialSiblingHardwareOrCreateItsGaps(bool deletePool)
    {
        var f = new Fixture(withPoolTopology: true);
        var source = new Source();
        var persistence = new Persistence();
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: new Resolver(f));
        Assert.True((await coordinator.StartAsync(f.Request, default)).IsSuccess);
        RealStorageCommand command = deletePool
            ? new DeletePoolCommand(RealTargetReference.ForExisting(f.Pool))
            : new CreatePoolCommand(RealTargetReference.ForExisting(f.Physical), "new-pool");
        var response = f.Response(command) with { State = RealOperationState.Running,
            Steps = [new("step", RealOperationStepState.CallIssued, null, null, null)] };
        await coordinator.ObserveAsync(response, default);
        Assert.True(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        Assert.All(source.Targets.Where(x => x.ObjectId != f.Physical), x => Assert.False(x.SuspendedForEdit));
        Assert.DoesNotContain(coordinator.CurrentDiagnostics.EditTargets, x => x.TargetId == f.Other || x.TargetId == f.Other2);

        var start = DateTimeOffset.UtcNow;
        foreach (var target in source.Targets.Where(x => x.SuspendedForEdit))
            await source.Availability!(new(f.Request.SessionId, target.ObjectId, target.CounterIdentity, false, start), default);
        // Two unaffected sample periods remain present while the WDC call is in progress.
        foreach (var at in new[] { start, start.AddSeconds(1) })
            foreach (var target in source.Targets.Where(x => !x.SuspendedForEdit))
            {
                await source.Availability!(new(f.Request.SessionId, target.ObjectId, target.CounterIdentity, true, at), default);
                source.Publish(new(f.Request.SessionId, target.ObjectId, at, [new(MonitorMetricKind.ActiveTimePercent, 42)]));
            }
        await WaitUntilAsync(() => persistence.Gaps.Count == 1 && persistence.SampleCount == 4);
        Assert.Equal(f.Physical, Assert.Single(persistence.Gaps.Values).TargetId);
        foreach (var id in new[] { f.Other, f.Other2 })
            Assert.Equal(new[] { start, start.AddSeconds(1) }, persistence.Samples.Where(x => x.TargetId == id).Select(x => x.SampledAtUtc));

        await coordinator.ObserveAsync(response with { State = RealOperationState.Failed,
            Steps = [new("step", RealOperationStepState.Failed, null, null, "{\"NoWindowsCall\":true}")] }, default);
        Assert.All(source.Targets, x => Assert.False(x.SuspendedForEdit));
        await source.Availability!(new(f.Request.SessionId, f.Physical, "disk-number:1", true, start.AddSeconds(2)), default);
        await WaitUntilAsync(() => persistence.Gaps.Values.Single().EndedAtUtc is not null);
        Assert.Single(persistence.Gaps);
        await coordinator.StopAsync(f.Request.SessionId, default);
    }

    [Fact]
    public async Task ConcretePoolDeletionStillPausesItsPhysicalMemberAndVirtualDisk()
    {
        var f = new Fixture(withPoolTopology: true);
        var virtualId = new StorageObjectId(f.System, StorageObjectKind.VirtualDisk, "vd");
        var request = f.Request with { Targets = f.Request.Targets.Append(new MonitorTarget(virtualId, "vd-guid:" + Guid.NewGuid())).ToArray() };
        var source = new Source();
        var coordinator = new MonitoringSessionCoordinator(source, new Persistence(),
            targetIdentityResolver: new Resolver(f) { InitialTargets = request.Targets });
        Assert.True((await coordinator.StartAsync(request, default)).IsSuccess);
        var response = f.Response(new DeletePoolCommand(RealTargetReference.ForExisting(f.Pool))) with
        { State = RealOperationState.Running, Steps = [new("step", RealOperationStepState.CallIssued, null, null, null)] };
        await coordinator.ObserveAsync(response, default);
        Assert.True(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        Assert.True(source.Targets.Single(x => x.ObjectId == virtualId).SuspendedForEdit);
        Assert.All(source.Targets.Where(x => x.ObjectId == f.Other || x.ObjectId == f.Other2), x => Assert.False(x.SuspendedForEdit));
        await coordinator.StopAsync(request.SessionId, default);
    }

    [Fact]
    public async Task AcceptedDoesNotCreateGapAndActualMissingEvidencePersistsUntilFirstRestoredSample()
    {
        var f = new Fixture();
        var source = new Source();
        var persistence = new Persistence();
        var resolver = new Resolver(f);
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: resolver);
        Assert.True((await coordinator.StartAsync(f.Request, default)).IsSuccess);
        var response = f.Response(new SetDiskOnlineCommand(RealTargetReference.ForExisting(f.Disk), true));
        await coordinator.ObserveAsync(response, default);
        Assert.All(source.Targets, x => Assert.False(x.SuspendedForEdit));
        Assert.Empty(persistence.Gaps);
        response = response with { State = RealOperationState.Running, Steps = [new("step", RealOperationStepState.CallIssued, null, null, null)] };
        await coordinator.ObserveAsync(response, default);
        Assert.True(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        Assert.False(source.Targets.Single(x => x.ObjectId == f.Other).SuspendedForEdit);
        var missingAt = DateTimeOffset.UtcNow;
        await source.Availability!(new(f.Request.SessionId, f.Physical, "disk-number:1", false, missingAt), default);
        source.Publish(new(f.Request.SessionId, f.Other, missingAt, [new(MonitorMetricKind.ActiveTimePercent, 42)]));
        await WaitUntilAsync(() => persistence.Gaps.Count == 1 && persistence.SampleCount == 1);
        Assert.Null(persistence.Gaps.Values.Single().EndedAtUtc);
        response = response with { State = RealOperationState.Succeeded, Steps = [new("step", RealOperationStepState.Verified, null, null, "{}")] };
        await coordinator.ObserveAsync(response, default);
        await coordinator.ObserveAsync(response, default);
        Assert.Equal(1, resolver.ScopedCalls);
        Assert.Null(persistence.Gaps.Values.Single().EndedAtUtc);
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status);
        var restoredAt = missingAt.AddSeconds(2);
        await source.Availability!(new(f.Request.SessionId, f.Physical, "disk-number:9", true, restoredAt), default);
        await WaitUntilAsync(() => persistence.Gaps.Values.Single().EndedAtUtc is not null);
        Assert.Equal(missingAt, persistence.Gaps.Values.Single().StartedAtUtc);
        Assert.Equal(restoredAt, persistence.Gaps.Values.Single().EndedAtUtc);
        Assert.Equal("disk-number:1", persistence.Gaps.Values.Single().CounterIdentity);
        await coordinator.ObserveAsync(response with { State = RealOperationState.Running,
            Steps = [new("step", RealOperationStepState.CallIssued, null, null, null)] }, default);
        Assert.Equal(MonitorEditTargetStatus.Restored, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status);
        Assert.False(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        await coordinator.StopAsync(f.Request.SessionId, default);
    }

    [Fact]
    public async Task VerifiedDeletionRetainsRemovedStatusAcrossLaterPlansAndKeepsUnrelatedSampling()
    {
        var f = new Fixture();
        var virtualId = new StorageObjectId(f.System, StorageObjectKind.VirtualDisk, "deleted-vd");
        var request = f.Request with { Targets = f.Request.Targets.Append(new MonitorTarget(virtualId, "vd-guid:" + Guid.NewGuid())).ToArray() };
        var source = new Source();
        var persistence = new Persistence();
        var resolver = new Resolver(f) { InitialTargets = request.Targets };
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: resolver);
        await coordinator.StartAsync(request, default);
        var response = f.Response(new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(virtualId))) with
        { State = RealOperationState.Succeeded, Steps = [new("step", RealOperationStepState.Verified, null, null, "{}")] };
        await coordinator.ObserveAsync(response, default);
        Assert.DoesNotContain(source.Targets, x => x.ObjectId == virtualId);
        Assert.Equal(MonitorEditTargetStatus.RemovedByEdit, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == virtualId).Status);
        await coordinator.ObserveAsync(response with { Plan = response.Plan with { OperationId = OperationId.New() }, State = RealOperationState.Accepted,
            Steps = [new("step", RealOperationStepState.Pending, null, null, null)] }, default);
        Assert.Equal(MonitorEditTargetStatus.RemovedByEdit, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == virtualId).Status);
        Assert.Contains(source.Targets, x => x.ObjectId == f.Other && !x.SuspendedForEdit);
        await coordinator.StopAsync(request.SessionId, default);
    }

    [Fact]
    public async Task UnknownOutcomeDoesNotInventGapEndpointOnStop()
    {
        var f = new Fixture();
        var source = new Source();
        var persistence = new Persistence();
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: new Resolver(f));
        await coordinator.StartAsync(f.Request, default);
        var response = f.Response(new SetDiskOnlineCommand(RealTargetReference.ForExisting(f.Disk), false)) with
        { State = RealOperationState.OutcomeUnknown, Steps = [new("step", RealOperationStepState.OutcomeUnknown, null, null, "{}")] };
        await coordinator.ObserveAsync(response, default);
        await source.Availability!(new(f.Request.SessionId, f.Physical, "disk-number:1", false, DateTimeOffset.UtcNow), default);
        await coordinator.StopAsync(f.Request.SessionId, default);
        Assert.Null(Assert.Single(persistence.Gaps.Values).EndedAtUtc);
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalOpenGapAllowsFreshThreeDeviceSamplingWithoutInventingOldEndpoint(bool reverseHistory)
    {
        var f = new Fixture();
        var request = f.Request with { Targets = f.Request.Targets.Append(new(f.Other2, "disk-number:3")).ToArray() };
        var old = HistoricalGap(f, DateTimeOffset.UtcNow.AddHours(-2));
        var latest = old with { GapId = "latest", SessionId = SessionId.New(), OperationId = OperationId.New(), StartedAtUtc = old.StartedAtUtc.AddHours(1) };
        var persistence = new Persistence { OpenHistory = reverseHistory ? [latest, old] : [old, latest] };
        var source = new Source();
        var resolver = new Resolver(f) { InitialTargets = request.Targets.Select(x => x with { ProviderIdentity = "fresh-exact-provider-and-os-identity" }).ToArray() };
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: resolver);
        await coordinator.InitializeEditRecoveryAsync();
        Assert.Equal(latest.OperationId, Assert.Single(coordinator.CurrentDiagnostics.EditTargets).OperationId);
        Assert.True((await coordinator.StartAsync(request, default)).IsSuccess);
        Assert.All(source.Targets, x => Assert.False(x.SuspendedForEdit));
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, Assert.Single(coordinator.CurrentDiagnostics.EditTargets).Status);
        Assert.Empty(persistence.Samples);
        Assert.Empty(persistence.Gaps);

        // A new session's actual missing counter opens its own interval. It
        // neither copies the historical start nor closes the historical gap.
        var missingAt = DateTimeOffset.UtcNow;
        await source.Availability!(new(request.SessionId, f.Physical, "disk-number:1", false, missingAt), default);
        await WaitUntilAsync(() => persistence.Gaps.Count == 1);
        var newGap = Assert.Single(persistence.Gaps.Values);
        Assert.Equal(request.SessionId, newGap.SessionId);
        Assert.Equal(missingAt, newGap.StartedAtUtc);
        Assert.Null(newGap.EndedAtUtc);
        var sampledAt = missingAt.AddSeconds(1);
        foreach (var target in source.Targets)
        {
            await source.Availability!(new(request.SessionId, target.ObjectId, target.CounterIdentity, true, sampledAt), default);
            source.Publish(new(request.SessionId, target.ObjectId, sampledAt, [new(MonitorMetricKind.ActiveTimePercent, 42)]));
        }
        await WaitUntilAsync(() => persistence.SampleCount == 3 && persistence.Gaps.Values.Single().EndedAtUtc == sampledAt
            && coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status == MonitorEditTargetStatus.Restored);
        Assert.Equal(3, persistence.Samples.Select(x => x.TargetId).Distinct().Count());
        Assert.All(persistence.OpenHistory, x => Assert.Null(x.EndedAtUtc));
        Assert.DoesNotContain(persistence.Gaps.Keys, x => x == old.GapId || x == latest.GapId);
        await coordinator.InitializeEditRecoveryAsync();
        Assert.Equal(MonitorEditTargetStatus.Restored, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status);
        await coordinator.StopAsync(request.SessionId, default);
    }

    [Theory]
    [InlineData(RealOperationStepState.OutcomeUnknown, false)]
    [InlineData(RealOperationStepState.CallIssued, false)]
    [InlineData(RealOperationStepState.OutcomeUnknown, true)]
    public async Task DurableOutstandingCallOverridesHistoricalGapAndStillSuspendsFreshBinding(RealOperationStepState stepState, bool historyLoadedLast)
    {
        var f = new Fixture();
        var source = new Source();
        var persistence = new Persistence { OpenHistory = [HistoricalGap(f, DateTimeOffset.UtcNow.AddHours(-1))] };
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: new Resolver(f)
        { InitialTargets = f.Request.Targets.Select(x => x with { ProviderIdentity = "fresh-exact-binding" }).ToArray() });
        if (!historyLoadedLast) await coordinator.InitializeEditRecoveryAsync();
        var response = f.Response(new SetDiskOnlineCommand(RealTargetReference.ForExisting(f.Disk), false)) with
        { State = stepState == RealOperationStepState.OutcomeUnknown ? RealOperationState.OutcomeUnknown : RealOperationState.Running,
            Steps = [new("step", stepState, null, null, "{}")] };
        await coordinator.ObserveAsync(response, default);
        if (historyLoadedLast) await coordinator.InitializeEditRecoveryAsync();
        Assert.True((await coordinator.StartAsync(f.Request, default)).IsSuccess);
        Assert.True(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        Assert.False(source.Targets.Single(x => x.ObjectId == f.Other).SuspendedForEdit);
        Assert.Equal(response.Plan.OperationId, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).OperationId);
        Assert.NotEqual("monitor.edit.recovered_endpoint_unknown", coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).ReasonCode);
        Assert.Empty(persistence.Samples);
        Assert.All(persistence.OpenHistory, x => Assert.Null(x.EndedAtUtc));
        await coordinator.StopAsync(f.Request.SessionId, default);
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("simulation")]
    [InlineData("scoped")]
    [InlineData("unresolved")]
    [InlineData("missing")]
    [InlineData("no-provider-identity")]
    [InlineData("empty-counter")]
    [InlineData("duplicate-family")]
    [InlineData("wrong-history-system")]
    public async Task HistoricalGapCannotResumeWithoutAnExactFreshResolvedBinding(string failure)
    {
        var f = new Fixture();
        var source = new Source();
        var resolver = new Resolver(f)
        { InitialTargets = f.Request.Targets.Select(x => x with { ProviderIdentity = "fresh-exact-binding" }).ToArray() };
        if (failure == "merged") resolver.InitialFacts = f.Facts with { IsMerged = true };
        if (failure == "simulation") resolver.InitialFacts = f.Facts with { IsSimulation = true };
        if (failure == "scoped")
        {
            var scope = StorageInventoryScopeFactory.Create(f.Facts, OperationId.New(), "other", [f.Physical]);
            resolver.InitialFacts = f.Facts with { ScopedCollection = new(scope, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false) };
        }
        if (failure == "unresolved") resolver.InitialUnresolved = [new(f.System, f.Physical, new OperationId(Guid.Empty), "initial",
            MonitorEditTargetStatus.NeedsSelection, "monitor.identity.exact_os_disk_association_unavailable", DateTimeOffset.UtcNow)];
        if (failure == "missing") resolver.InitialTargets = resolver.InitialTargets!.Where(x => x.ObjectId != f.Physical).ToArray();
        if (failure == "no-provider-identity") resolver.InitialTargets = f.Request.Targets;
        if (failure == "empty-counter") resolver.InitialTargets = resolver.InitialTargets!.Select(x => x.ObjectId == f.Physical ? x with { CounterIdentity = "" } : x).ToArray();
        if (failure == "duplicate-family") resolver.InitialTargets = resolver.InitialTargets!.Append(resolver.InitialTargets![0] with { CounterIdentity = "disk-number:9" }).ToArray();
        var historical = HistoricalGap(f, DateTimeOffset.UtcNow.AddHours(-1));
        if (failure == "wrong-history-system") historical = historical with { SystemId = SystemId.New() };
        var persistence = new Persistence { OpenHistory = [historical] };
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: resolver);
        await coordinator.InitializeEditRecoveryAsync();
        Assert.True((await coordinator.StartAsync(f.Request, default)).IsSuccess);
        Assert.DoesNotContain(source.Targets, x => x.ObjectId == f.Physical && !x.SuspendedForEdit);
        Assert.NotEqual(MonitorEditTargetStatus.Restored, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status);
        Assert.Empty(persistence.Samples);
        Assert.Empty(persistence.Gaps);
        Assert.Null(Assert.Single(persistence.OpenHistory).EndedAtUtc);
        await coordinator.StopAsync(f.Request.SessionId, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredOrVerifiedInactiveVirtualDiskRequiresActualSamplingOfBothCurrentCounterFamilies(bool verifiedWithoutSampler)
    {
        var f = new Fixture();
        var vd = new StorageObjectId(f.System, StorageObjectKind.VirtualDisk, "current-vd");
        var throughput = new MonitorTarget(vd, "disk-number:9") { ProviderIdentity = "exact-vd-and-current-os", CounterSource = MonitorCounterSource.PhysicalDisk };
        var stateCounter = new MonitorTarget(vd, "vd-disk-number:9") { ProviderIdentity = "exact-vd-and-current-os", CounterSource = MonitorCounterSource.StorageSpacesVirtualDisk };
        var request = f.Request with { Targets = [throughput, stateCounter], Metrics = [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.VirtualDiskActiveBytes] };
        var old = HistoricalGap(f, DateTimeOffset.UtcNow.AddHours(-1)) with { TargetId = vd };
        var oldState = old with { GapId = "old-state", CounterIdentity = "vd-disk-number:4" };
        var persistence = new Persistence { OpenHistory = verifiedWithoutSampler ? [] : [oldState, old] };
        var source = new Source();
        var coordinator = new MonitoringSessionCoordinator(source, persistence,
            targetIdentityResolver: new Resolver(f) { InitialTargets = request.Targets });
        await coordinator.InitializeEditRecoveryAsync();
        if (verifiedWithoutSampler)
        {
            var verified = f.Response(new SetDiskOnlineCommand(RealTargetReference.ForExisting(f.Disk), true)) with
            { State = RealOperationState.Succeeded, Steps = [new("step", RealOperationStepState.Verified, null, null, "{}")] };
            verified = verified with { Plan = verified.Plan with { Targets = [vd, f.Disk] } };
            await coordinator.ObserveAsync(verified, default);
            Assert.Equal("monitor.edit.verified_without_active_sampler", Assert.Single(coordinator.CurrentDiagnostics.EditTargets).ReasonCode);
        }
        Assert.True((await coordinator.StartAsync(request, default)).IsSuccess);
        Assert.All(source.Targets, x => Assert.False(x.SuspendedForEdit));
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, Assert.Single(coordinator.CurrentDiagnostics.EditTargets).Status);
        var missing = DateTimeOffset.UtcNow;
        foreach (var target in source.Targets)
            await source.Availability!(new(request.SessionId, vd, target.CounterIdentity, false, missing), default);
        await WaitUntilAsync(() => persistence.Gaps.Count == 2);
        var actual = missing.AddSeconds(1);
        await source.Availability!(new(request.SessionId, vd, throughput.CounterIdentity, true, actual), default);
        source.Publish(new(request.SessionId, vd, actual, [new(MonitorMetricKind.ActiveTimePercent, 42)]) { CounterSource = MonitorCounterSource.PhysicalDisk });
        await WaitUntilAsync(() => persistence.SampleCount == 1 && persistence.Gaps.Values.Single(x => x.CounterIdentity == throughput.CounterIdentity).EndedAtUtc is not null);
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, Assert.Single(coordinator.CurrentDiagnostics.EditTargets).Status);
        Assert.Null(persistence.Gaps.Values.Single(x => x.CounterIdentity == stateCounter.CounterIdentity).EndedAtUtc);
        await source.Availability!(new(request.SessionId, vd, stateCounter.CounterIdentity, true, actual.AddSeconds(1)), default);
        source.Publish(new(request.SessionId, vd, actual.AddSeconds(1), [new(MonitorMetricKind.VirtualDiskActiveBytes, 1024)]) { CounterSource = MonitorCounterSource.StorageSpacesVirtualDisk });
        await WaitUntilAsync(() => persistence.SampleCount == 2 && coordinator.CurrentDiagnostics.EditTargets.Single().Status == MonitorEditTargetStatus.Restored);
        Assert.All(persistence.Gaps.Values, x => Assert.NotNull(x.EndedAtUtc));
        Assert.All(persistence.OpenHistory, x => Assert.Null(x.EndedAtUtc));
        Assert.Equal(2, persistence.Samples.Select(x => x.CounterSource).Distinct().Count());
        await coordinator.StopAsync(request.SessionId, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedBindingOrNoCallPendingStateCanResumeAfterMonitoringRestarts(bool noWindowsCall)
    {
        var f = new Fixture();
        var source = new Source();
        var persistence = new Persistence();
        var coordinator = new MonitoringSessionCoordinator(source, persistence, targetIdentityResolver: new Resolver(f)
        { InitialTargets = f.Request.Targets.Select(x => x with { ProviderIdentity = "fresh-exact-binding" }).ToArray() });
        Assert.True((await coordinator.StartAsync(f.Request, default)).IsSuccess);
        var response = f.Response(new SetDiskOnlineCommand(RealTargetReference.ForExisting(f.Disk), true)) with
        { State = RealOperationState.Running, Steps = [new("step", RealOperationStepState.CallIssued, null, null, null)] };
        await coordinator.ObserveAsync(response, default);
        response = response with { State = noWindowsCall ? RealOperationState.Failed : RealOperationState.Succeeded,
            Steps = [new("step", noWindowsCall ? RealOperationStepState.Failed : RealOperationStepState.Verified, null, null,
                noWindowsCall ? "{\"NoWindowsCall\":true}" : "{}")] };
        await coordinator.ObserveAsync(response, default);
        Assert.Equal(noWindowsCall ? "monitor.edit.unchanged_waiting_sample" : "monitor.edit.binding_verified_waiting_sample",
            coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).ReasonCode);
        await coordinator.StopAsync(f.Request.SessionId, default);
        var next = f.Request with { SessionId = SessionId.New() };
        Assert.True((await coordinator.StartAsync(next, default)).IsSuccess);
        Assert.False(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status);
        Assert.Empty(persistence.Samples);
        var at = DateTimeOffset.UtcNow;
        await source.Availability!(new(next.SessionId, f.Physical, "disk-number:1", true, at), default);
        source.Publish(new(next.SessionId, f.Physical, at, [new(MonitorMetricKind.ActiveTimePercent, 42)]));
        await WaitUntilAsync(() => persistence.SampleCount == 1
            && coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).Status == MonitorEditTargetStatus.Restored);
        await coordinator.StopAsync(next.SessionId, default);
    }

    private static MonitorEditGap HistoricalGap(Fixture f, DateTimeOffset started) =>
        new("old", SessionId.New(), f.System, f.Physical, "disk-number:8", OperationId.New(), "create-pool", started,
            null, MonitorEditTargetStatus.PendingVerification, "monitor.edit.binding_verified_waiting_sample");

    [Fact]
    public async Task MissingBindingCannotEraseOutstandingOperationAndAllowItOnTheNextSession()
    {
        var f = new Fixture();
        var source = new Source();
        var resolver = new Resolver(f) { InitialTargets = [], InitialUnresolved = [new(f.System, f.Physical,
            new OperationId(Guid.Empty), "initial", MonitorEditTargetStatus.NeedsSelection, "monitor.identity.provider_identity_unavailable", DateTimeOffset.UtcNow)] };
        var coordinator = new MonitoringSessionCoordinator(source, new Persistence(), targetIdentityResolver: resolver);
        var response = f.Response(new SetDiskOnlineCommand(RealTargetReference.ForExisting(f.Disk), false)) with
        { State = RealOperationState.OutcomeUnknown, Steps = [new("step", RealOperationStepState.OutcomeUnknown, null, null, "{}")] };
        await coordinator.ObserveAsync(response, default);
        Assert.True((await coordinator.StartAsync(f.Request, default)).IsSuccess);
        Assert.Equal(response.Plan.OperationId, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).OperationId);
        await coordinator.StopAsync(f.Request.SessionId, default);
        resolver.InitialTargets = f.Request.Targets.Select(x => x with { ProviderIdentity = "fresh-current-binding" }).ToArray();
        resolver.InitialUnresolved = [];
        var next = f.Request with { SessionId = SessionId.New() };
        Assert.True((await coordinator.StartAsync(next, default)).IsSuccess);
        Assert.True(source.Targets.Single(x => x.ObjectId == f.Physical).SuspendedForEdit);
        Assert.Equal(response.Plan.OperationId, coordinator.CurrentDiagnostics.EditTargets.Single(x => x.TargetId == f.Physical).OperationId);
        await coordinator.StopAsync(next.SessionId, default);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture
    {
        public SystemId System { get; } = SystemId.New();
        public StorageObjectId Physical => new(System, StorageObjectKind.PhysicalDisk, "physical");
        public StorageObjectId Other => new(System, StorageObjectKind.PhysicalDisk, "other");
        public StorageObjectId Other2 => new(System, StorageObjectKind.PhysicalDisk, "other2");
        public StorageObjectId Pool => new(System, StorageObjectKind.StoragePool, "concrete");
        public StorageObjectId Disk => new(System, StorageObjectKind.OsDisk, "disk");
        public MonitorRequest Request { get; }
        public WinPoolFacts Facts { get; }
        public Fixture(bool withPoolTopology = false)
        {
            Request = new(SessionId.New(), System, [new(Physical, "disk-number:1"), new(Other, "disk-number:2")],
                [MonitorMetricKind.ActiveTimePercent], TimeSpan.FromSeconds(1), true);
            var source = new WinPoolSource("source", FactOrigin.StorageCim, "root/microsoft/windows/storage", "MSFT_PhysicalDisk", DateTimeOffset.UtcNow, CollectionPurpose.Storage);
            Facts = new(1, System, 0, [source],
                [new("physical", FactObjectType.PhysicalDisk, source.Id, "physical-uid", true,
                    [WinPoolSourceField.Returned("UniqueId", "physical-uid", FactValueType.String, source.Id)]),
                 new("disk", FactObjectType.Disk, source.Id, "disk-uid", true, [])],
                [new("physical", "disk", "same-device")], [], []);
            if (withPoolTopology)
            {
                Request = Request with { Targets = Request.Targets.Append(new(Other2, "disk-number:3")).ToArray() };
                Facts = Facts with
                {
                    Objects = [.. Facts.Objects,
                        new("other", FactObjectType.PhysicalDisk, source.Id, "other-uid", true, []),
                        new("other2", FactObjectType.PhysicalDisk, source.Id, "other2-uid", true, []),
                        new("primordial", FactObjectType.StoragePool, source.Id, "primordial-uid", true,
                            [WinPoolSourceField.Returned("IsPrimordial", true, FactValueType.Boolean, source.Id)]),
                        new("concrete", FactObjectType.StoragePool, source.Id, "concrete-uid", true,
                            [WinPoolSourceField.Returned("IsPrimordial", false, FactValueType.Boolean, source.Id)]),
                        new("vd", FactObjectType.VirtualDisk, source.Id, "vd-uid", true, [])],
                    Relationships = [.. Facts.Relationships,
                        new("primordial", "physical", "pool-member"), new("primordial", "other", "pool-member"),
                        new("primordial", "other2", "pool-member"), new("concrete", "physical", "pool-member"),
                        new("concrete", "vd", "pool-virtual-disk")]
                };
            }
        }
        public AgentRealOperationResponse Response(RealStorageCommand command)
        {
            var plan = OperationPlan.Create(new(OperationId.New(), EnvironmentId.New(), System, OperationIntent.SetDiskOnlineState,
                [Physical, Disk], new Dictionary<string, string>(), DateTimeOffset.UtcNow), ExecutionCapability.MutateStorageStructure,
                RiskLevel.R4StorageStructureMutation, "current", [], [new("step", "online", [])], null, "disk", "", "",
                RealOperationPlanFactory.Algorithm, DateTimeOffset.UtcNow);
            plan = plan with { RealOperation = new(1, "adapter", "machine", "session", "target", "physical", "evidence", "online",
                DateTimeOffset.UtcNow.AddMinutes(1), [new("step", command, [], "before", "after", "", "evidence")]) };
            return new(plan, RealOperationState.Accepted, [new("step", RealOperationStepState.Pending, null, null, null)], null, false);
        }
    }

    private sealed class Resolver(Fixture f) : IMonitorTargetIdentityResolver
    {
        public WinPoolFacts? InitialFacts;
        public IReadOnlyList<MonitorEditTargetState> InitialUnresolved = [];
        public int ScopedCalls;
        public IReadOnlyList<MonitorTarget>? InitialTargets;
        public Task<MonitorTargetResolution> ResolveAsync(MonitorRequest request, StorageInventoryScope? scope, CancellationToken ct)
        {
            if (scope is not null) Interlocked.Increment(ref ScopedCalls);
            var facts = scope is null ? InitialFacts ?? f.Facts : f.Facts with { ScopedCollection = new(scope, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true) };
            IReadOnlyList<MonitorTarget> targets = scope is null ? InitialTargets ?? f.Request.Targets : [new(f.Physical, "disk-number:9")];
            return Task.FromResult(new MonitorTargetResolution(f.System, facts, targets, scope is null ? InitialUnresolved : []));
        }
    }
    private sealed class Source : IMonitorSource, IRebindableMonitorSource
    {
        private readonly Channel<MonitorSample> channel = Channel.CreateUnbounded<MonitorSample>();
        public IReadOnlyList<MonitorTarget> Targets = [];
        public Func<MonitorTargetAvailability, CancellationToken, Task>? Availability;
        public void SetTargets(SessionId session, IReadOnlyList<MonitorTarget> targets) => Targets = targets;
        public void ReleaseTargets(SessionId session) { }
        public void SetAvailabilityObserver(Func<MonitorTargetAvailability, CancellationToken, Task> observer) => Availability = observer;
        public void Publish(MonitorSample sample) => channel.Writer.TryWrite(sample);
        public async IAsyncEnumerable<MonitorSample> SampleAsync(MonitorRequest request, [EnumeratorCancellation] CancellationToken ct)
        { await foreach (var sample in channel.Reader.ReadAllAsync(ct)) yield return sample; }
    }
    private sealed class Persistence : IMonitorSessionPersistenceFactory, IMonitorSessionPersistence, IMonitorEditGapPersistence, IMonitorEditGapHistory
    {
        public IReadOnlyList<MonitorEditGap> OpenHistory = [];
        public Task<IReadOnlyList<MonitorEditGap>> LoadOpenEditGapsAsync(CancellationToken ct) => Task.FromResult(OpenHistory);
        public ConcurrentDictionary<string, MonitorEditGap> Gaps = new();
        public ConcurrentQueue<MonitorSample> Samples = new();
        public int SampleCount;
        public IMonitorSessionPersistence Create(SessionId session) => this;
        public Task StartAsync(MonitoringSession session, CancellationToken ct) => Task.CompletedTask;
        public bool TryWrite(MonitorSample sample) { Samples.Enqueue(sample); Interlocked.Increment(ref SampleCount); return true; }
        public Task SaveEditGapAsync(MonitorEditGap gap, CancellationToken ct) { Gaps[gap.GapId] = gap; return Task.CompletedTask; }
        public Task AddDroppedSamplesAsync(long count, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(MonitoringSessionState state, DateTimeOffset ended, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
