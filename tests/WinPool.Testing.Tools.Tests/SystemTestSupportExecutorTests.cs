using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Testing.Tools.Tests;

public sealed class SystemTestSupportExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReleaseRequiresConfirmationBeforeAnyPortIsCalled()
    {
        var fixture = Fixture.Create();
        fixture.RuntimePolicy.Snapshot = new(true, "rules-1");
        var action = fixture.Authorize(
            new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                fixture.RamMap.Identity));

        var result = await fixture.Executor.ExecuteAsync(
            action,
            new SystemSupportExecutionOptions(true, false, "rules-1"),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal(
            "system-support.release-confirmation-required",
            Assert.Single(result.Messages).Code);
        Assert.Equal(0, fixture.RamMap.ClearCount);
        Assert.Contains(
            fixture.Audit.Events,
            item => item.Stage == SystemSupportAuditStage.Rejected);
    }

    [Fact]
    public async Task DevelopmentAllowsConfirmedIdentityRamMapOnlyThroughFixedClearMode()
    {
        var fixture = Fixture.Create();
        var action = fixture.Authorize(
            new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                fixture.RamMap.Identity));

        var result = await fixture.Executor.ExecuteAsync(
            action,
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.Equal(1, fixture.RamMap.ClearCount);
        Assert.Equal(
            RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
            fixture.RamMap.LastMode);
        Assert.Contains(
            fixture.Audit.Events,
            item => item.Code == "system-support.rammap.evidence-complete");
    }

    [Fact]
    public async Task RamMapMissingChangedOrUntrustedIdentityIsRejected()
    {
        var missing = Fixture.Create();
        var planned = missing.RamMap.Identity;
        missing.RamMap.Identity = null;
        var missingResult = await missing.Executor.ExecuteAsync(
            missing.Authorize(new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                planned)),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        var changed = Fixture.Create();
        var oldIdentity = changed.RamMap.Identity;
        changed.RamMap.Identity = oldIdentity! with { Sha256 = new string('b', 64) };
        var changedResult = await changed.Executor.ExecuteAsync(
            changed.Authorize(new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                oldIdentity)),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(
            "system-support.rammap.missing",
            Assert.Single(missingResult.Messages).Code);
        Assert.Equal(
            "system-support.rammap.identity-changed",
            Assert.Single(changedResult.Messages).Code);
        Assert.Equal(0, missing.RamMap.ClearCount);
        Assert.Equal(0, changed.RamMap.ClearCount);
    }

    [Fact]
    public async Task TemporaryCleanupIsReviewedAndProtectedCandidatesNeverReachDeletePort()
    {
        var fixture = Fixture.Create();
        var safe = fixture.Candidate(
            "safe",
            Path.Combine(fixture.UserTemp, "ordinary.tmp"),
            TemporaryFileScope.CurrentUserTemporaryFiles);
        var update = fixture.Candidate(
            "update",
            Path.Combine(fixture.Windows, "SoftwareDistribution", "update.bin"),
            TemporaryFileScope.WindowsOrdinaryTemporaryFiles);
        var installer = fixture.Candidate(
            "installer",
            Path.Combine(fixture.Windows, "Installer", "package.msi"),
            TemporaryFileScope.WindowsOrdinaryTemporaryFiles);
        var protectedFile = fixture.Candidate(
            "wrp",
            Path.Combine(fixture.UserTemp, "protected.tmp"),
            TemporaryFileScope.CurrentUserTemporaryFiles,
            isWindowsResourceProtected: true);
        var reparse = fixture.Candidate(
            "link",
            Path.Combine(fixture.UserTemp, "linked.tmp"),
            TemporaryFileScope.CurrentUserTemporaryFiles,
            isReparsePoint: true);
        fixture.TemporaryFiles.Candidates = [safe, update, installer, protectedFile, reparse];
        var action = fixture.Authorize(
            new CleanTemporaryFilesAction(
                [
                    TemporaryFileScope.CurrentUserTemporaryFiles,
                    TemporaryFileScope.WindowsOrdinaryTemporaryFiles
                ]));

        var review = await fixture.Executor.ReviewTemporaryCleanupAsync(
            action,
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);
        var execution = await fixture.Executor.ExecuteTemporaryCleanupAsync(
            action,
            Assert.IsType<TemporaryCleanupReview>(review.Value),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Succeeded, execution.Status);
        Assert.Equal([safe.Id], fixture.TemporaryFiles.Cleaned.Select(item => item.Id));
        Assert.Equal(5, execution.Value!.CleanupItems.Count);
        Assert.All(
            execution.Value.CleanupItems.Where(item => item.CandidateId != safe.Id),
            item => Assert.Equal(TemporaryCleanupItemStatus.Skipped, item.Status));
    }

    [Fact]
    public async Task TemporaryCleanupCandidateChangeInvalidatesApprovedReview()
    {
        var fixture = Fixture.Create();
        fixture.TemporaryFiles.Candidates =
        [
            fixture.Candidate(
                "safe",
                Path.Combine(fixture.UserTemp, "ordinary.tmp"),
                TemporaryFileScope.CurrentUserTemporaryFiles)
        ];
        var action = fixture.Authorize(
            new CleanTemporaryFilesAction(
                [TemporaryFileScope.CurrentUserTemporaryFiles]));
        var review = await fixture.Executor.ReviewTemporaryCleanupAsync(
            action,
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);
        fixture.TemporaryFiles.Candidates =
        [
            fixture.Candidate(
                "changed",
                Path.Combine(fixture.UserTemp, "other.tmp"),
                TemporaryFileScope.CurrentUserTemporaryFiles)
        ];

        var result = await fixture.Executor.ExecuteTemporaryCleanupAsync(
            action,
            review.Value!,
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal(
            "system-support.temporary-cleanup.candidates-changed",
            Assert.Single(result.Messages).Code);
        Assert.Empty(fixture.TemporaryFiles.Cleaned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolumeTargetIsResolvedFromPlanAndAgainBeforeFlushOrOptimize(
        bool optimize)
    {
        var fixture = Fixture.Create();
        var volume = new StorageObjectId(
            SystemId.New(),
            StorageObjectKind.Partition,
            "volume-1");
        var snapshot = new VolumeTargetSnapshot(volume, "stable-volume-1", "redacted-volume");
        fixture.Volumes.Planned = snapshot;
        fixture.Volumes.Current = snapshot;
        SystemSupportAction requested = optimize
            ? new TrimOrOptimizeVolumeAction(volume)
            : new FlushVolumeAction(volume);

        var result = await fixture.Executor.ExecuteAsync(
            fixture.Authorize(requested),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.Equal(optimize ? 0 : 1, fixture.Volumes.FlushCount);
        Assert.Equal(optimize ? 1 : 0, fixture.Volumes.OptimizeCount);
        Assert.Equal(1, fixture.Volumes.PlannedResolveCount);
        Assert.Equal(1, fixture.Volumes.CurrentResolveCount);
    }

    [Fact]
    public async Task ChangedVolumeIdentityIsRejectedBeforeMaintenance()
    {
        var fixture = Fixture.Create();
        var volume = new StorageObjectId(
            SystemId.New(),
            StorageObjectKind.Partition,
            "volume-1");
        fixture.Volumes.Planned =
            new VolumeTargetSnapshot(volume, "planned", "redacted");
        fixture.Volumes.Current =
            new VolumeTargetSnapshot(volume, "changed", "redacted");

        var result = await fixture.Executor.ExecuteAsync(
            fixture.Authorize(new FlushVolumeAction(volume)),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal(
            "system-support.volume.target-changed",
            Assert.Single(result.Messages).Code);
        Assert.Equal(0, fixture.Volumes.FlushCount);
    }

    [Fact]
    public async Task SchedulingCanOnlyTouchRegisteredTestProcessesAndRestoresOnSuccess()
    {
        var fixture = Fixture.Create();
        fixture.Scheduling.Snapshots[41] = new(
            41,
            true,
            TestProcessPriority.Normal,
            [0, 1, 2, 3]);
        var workRan = false;

        var result = await fixture.Executor.ExecuteScopedAsync(
            fixture.Authorize(new AdjustProcessSchedulingAction(
                [41],
                TestProcessPriority.High,
                [1, 3])),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            _ =>
            {
                workRan = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.True(workRan);
        Assert.Equal([41], fixture.Scheduling.AppliedProcessIds);
        Assert.Equal([41], fixture.Scheduling.RestoredProcessIds);
        Assert.Empty(fixture.Recovery.Entries);

        fixture.Scheduling.Snapshots[99] = new(
            99,
            false,
            TestProcessPriority.Normal,
            [0]);
        var rejected = await fixture.Executor.ExecuteScopedAsync(
            fixture.Authorize(new AdjustProcessSchedulingAction(
                [99],
                TestProcessPriority.High,
                [0])),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            _ => Task.CompletedTask,
            CancellationToken.None);
        Assert.Equal(ApplicationStatus.Rejected, rejected.Status);
        Assert.DoesNotContain(99, fixture.Scheduling.AppliedProcessIds);
    }

    [Fact]
    public async Task SchedulingRestoresOnCancellationAndFailure()
    {
        var cancelled = Fixture.Create();
        cancelled.Scheduling.Snapshots[41] = new(
            41,
            true,
            TestProcessPriority.Normal,
            [0]);
        using var cancellation = new CancellationTokenSource();
        var cancelledResult = await cancelled.Executor.ExecuteScopedAsync(
            cancelled.Authorize(new AdjustProcessSchedulingAction(
                [41],
                TestProcessPriority.AboveNormal,
                [0])),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cancellation.Token);

        var failed = Fixture.Create();
        failed.Scheduling.Snapshots[42] = new(
            42,
            true,
            TestProcessPriority.Normal,
            [0]);
        var failedResult = await failed.Executor.ExecuteScopedAsync(
            failed.Authorize(new AdjustProcessSchedulingAction(
                [42],
                TestProcessPriority.AboveNormal,
                [0])),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            _ => throw new InvalidOperationException("injected"),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Cancelled, cancelledResult.Status);
        Assert.Equal([41], cancelled.Scheduling.RestoredProcessIds);
        Assert.Empty(cancelled.Recovery.Entries);
        Assert.Equal(ApplicationStatus.Failed, failedResult.Status);
        Assert.Equal([42], failed.Scheduling.RestoredProcessIds);
        Assert.Empty(failed.Recovery.Entries);
    }

    [Fact]
    public async Task TemporaryPowerPlanRestoresOnSuccessFailureAndCancellation()
    {
        var fixture = Fixture.Create();
        var requestedPlan = Guid.NewGuid();
        var success = await fixture.Executor.ExecuteScopedAsync(
            fixture.Authorize(new UseTemporaryPowerPlanAction(requestedPlan)),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            _ => Task.CompletedTask,
            CancellationToken.None);
        var failure = await fixture.Executor.ExecuteScopedAsync(
            fixture.Authorize(new UseTemporaryPowerPlanAction(requestedPlan)),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            _ => throw new InvalidOperationException("injected"),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = await fixture.Executor.ExecuteScopedAsync(
            fixture.Authorize(new UseTemporaryPowerPlanAction(requestedPlan)),
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(ApplicationStatus.Succeeded, success.Status);
        Assert.Equal(ApplicationStatus.Failed, failure.Status);
        Assert.Equal(ApplicationStatus.Cancelled, cancelled.Status);
        Assert.Equal(3, fixture.PowerPlans.Activated.Count);
        Assert.Equal(3, fixture.PowerPlans.Restored.Count);
        Assert.Empty(fixture.Recovery.Entries);
    }

    [Fact]
    public async Task PendingCrashRecoveryRestoresTypedStateAndRetainsFailedEntries()
    {
        var fixture = Fixture.Create();
        var processSnapshot = new TestProcessSchedulingSnapshot(
            41,
            true,
            TestProcessPriority.Normal,
            [0, 1]);
        var processEntry = new SystemSupportRecoveryEntry(
            Guid.NewGuid(),
            "plan-process",
            SystemSupportActionKind.AdjustProcessScheduling,
            new ProcessSchedulingRecoveryState(processSnapshot),
            Now.AddMinutes(-2));
        var powerEntry = new SystemSupportRecoveryEntry(
            Guid.NewGuid(),
            "plan-power",
            SystemSupportActionKind.UseTemporaryPowerPlan,
            new PowerPlanRecoveryState(new PowerPlanSnapshot(Guid.NewGuid())),
            Now.AddMinutes(-1));
        fixture.Recovery.Entries.Add(processEntry);
        fixture.Recovery.Entries.Add(powerEntry);
        fixture.PowerPlans.ThrowOnRestore = true;

        var result = await fixture.Executor.RecoverPendingAsync(
            SystemSupportExecutionOptions.Development(),
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.PartiallyCompleted, result.Status);
        Assert.Equal(1, result.Value!.RestoredCount);
        Assert.Equal([powerEntry.RecoveryId], result.Value.FailedRecoveryIds);
        Assert.Equal([41], fixture.Scheduling.RestoredProcessIds);
        Assert.Single(fixture.Recovery.Entries);
        Assert.Equal(powerEntry.RecoveryId, fixture.Recovery.Entries[0].RecoveryId);
        Assert.Contains(
            fixture.Audit.Events,
            item => item.Stage == SystemSupportAuditStage.RecoveryFailed);
    }

    private sealed class Fixture
    {
        private Fixture(string root)
        {
            Root = root;
            WinPoolTemp = Path.Combine(root, "WinPoolTemp");
            UserTemp = Path.Combine(root, "UserTemp");
            Windows = Path.Combine(root, "Windows");
            WindowsTemp = Path.Combine(Windows, "Temp");
            TemporaryFiles = new FakeTemporaryFiles();
            RamMap = new FakeRamMap
            {
                Identity = new RamMapToolIdentity(
                    new string('c', 64),
                    "1.61",
                    "Microsoft Corporation",
                    new string('a', 64),
                    true)
            };
            Volumes = new FakeVolumes();
            Scheduling = new FakeScheduling();
            PowerPlans = new FakePowerPlans();
            Recovery = new FakeRecovery();
            Audit = new FakeAudit();
            Executor = new SystemTestSupportExecutor(
                new SystemTestSupportPorts(
                    RuntimePolicy,
                    TemporaryFiles,
                    new TemporaryCleanupPathPolicy(
                        new TemporaryCleanupRoots(
                            WinPoolTemp,
                            UserTemp,
                            WindowsTemp,
                            Windows,
                            [Path.Combine(UserTemp, "ProtectedAppData")])),
                    RamMap,
                    Volumes,
                    Scheduling,
                    PowerPlans,
                    Recovery,
                    Audit),
                new FixedTimeProvider(Now));
        }

        public string Root { get; }
        public string WinPoolTemp { get; }
        public string UserTemp { get; }
        public string Windows { get; }
        public string WindowsTemp { get; }
        public FakeTemporaryFiles TemporaryFiles { get; }
        public FakeRuntimePolicy RuntimePolicy { get; } = new();
        public FakeRamMap RamMap { get; }
        public FakeVolumes Volumes { get; }
        public FakeScheduling Scheduling { get; }
        public FakePowerPlans PowerPlans { get; }
        public FakeRecovery Recovery { get; }
        public FakeAudit Audit { get; }
        public SystemTestSupportExecutor Executor { get; }

        public static Fixture Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                "WinPool.SystemSupport.Tests",
                Guid.NewGuid().ToString("N")));

        public AuthorizedSystemSupportAction Authorize(SystemSupportAction action) =>
            new(action, $"plan-{Guid.NewGuid():N}", Now.AddMinutes(5));

        public TemporaryCleanupCandidate Candidate(
            string id,
            string path,
            TemporaryFileScope scope,
            bool isReparsePoint = false,
            bool isWindowsResourceProtected = false) =>
            new(
                new TemporaryCleanupCandidateId(id),
                path,
                scope,
                128,
                isReparsePoint,
                isWindowsResourceProtected);
    }

    private sealed class FakeRuntimePolicy : ISystemSupportRuntimePolicy
    {
        public SystemSupportRuntimePolicySnapshot Snapshot { get; set; } =
            new(false, "system-support-v1");

        public SystemSupportRuntimePolicySnapshot GetCurrent() => Snapshot;
    }

    private sealed class FakeTemporaryFiles : ITemporaryFileCleanupPort
    {
        public IReadOnlyList<TemporaryCleanupCandidate> Candidates { get; set; } = [];
        public List<TemporaryCleanupCandidate> Cleaned { get; } = [];

        public Task<IReadOnlyList<TemporaryCleanupCandidate>> ScanAsync(
            IReadOnlyList<TemporaryFileScope> scopes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Candidates);
        }

        public Task<TemporaryCleanupPortResult> CleanAsync(
            IReadOnlyList<TemporaryCleanupCandidate> approvedCandidates,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cleaned.AddRange(approvedCandidates);
            return Task.FromResult(
                new TemporaryCleanupPortResult(
                    approvedCandidates
                        .Select(item => new TemporaryCleanupItemResult(
                            item.Id,
                            TemporaryCleanupItemStatus.Removed,
                            "removed"))
                        .ToArray()));
        }
    }

    private sealed class FakeRamMap : IRamMapCacheClearPort
    {
        public bool SupportsElevatedBroker { get; set; } = true;
        public RamMapToolIdentity? Identity { get; set; }
        public int ClearCount { get; private set; }
        public RamMapCacheClearMode? LastMode { get; private set; }

        public Task<RamMapToolIdentity?> DetectIdentityAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Identity);
        }

        public Task<RamMapCacheClearEvidence> ClearAsync(
            RamMapCacheClearRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCount++;
            LastMode = request.Mode;
            return Task.FromResult(
                new RamMapCacheClearEvidence(
                    Array.AsReadOnly(["-Es", "-Et"]),
                    0,
                    string.Empty,
                    string.Empty,
                    "before",
                    "after",
                    request.RequiresElevatedBroker));
        }
    }

    private sealed class FakeVolumes : IVolumeMaintenancePort
    {
        public VolumeTargetSnapshot? Planned { get; set; }
        public VolumeTargetSnapshot? Current { get; set; }
        public int PlannedResolveCount { get; private set; }
        public int CurrentResolveCount { get; private set; }
        public int FlushCount { get; private set; }
        public int OptimizeCount { get; private set; }

        public Task<VolumeTargetSnapshot?> ResolvePlannedTargetAsync(
            StorageObjectId volumeId,
            string planHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlannedResolveCount++;
            return Task.FromResult(Planned);
        }

        public Task<VolumeTargetSnapshot?> ResolveCurrentTargetAsync(
            StorageObjectId volumeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentResolveCount++;
            return Task.FromResult(Current);
        }

        public Task<VolumeMaintenanceEvidence> FlushAsync(
            VolumeTargetSnapshot target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.FromResult(new VolumeMaintenanceEvidence("fake-flush", string.Empty));
        }

        public Task<VolumeMaintenanceEvidence> TrimOrOptimizeAsync(
            VolumeTargetSnapshot target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OptimizeCount++;
            return Task.FromResult(new VolumeMaintenanceEvidence("fake-optimize", string.Empty));
        }
    }

    private sealed class FakeScheduling : ITestProcessSchedulingPort
    {
        public Dictionary<int, TestProcessSchedulingSnapshot> Snapshots { get; } = [];
        public List<int> AppliedProcessIds { get; } = [];
        public List<int> RestoredProcessIds { get; } = [];

        public Task<TestProcessSchedulingSnapshot?> CaptureAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.TryGetValue(processId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task ApplyAsync(
            int processId,
            TestProcessPriority priority,
            IReadOnlyList<int> logicalProcessorIndices,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppliedProcessIds.Add(processId);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(
            TestProcessSchedulingSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoredProcessIds.Add(snapshot.ProcessId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePowerPlans : ITemporaryPowerPlanPort
    {
        public PowerPlanSnapshot Active { get; } = new(Guid.NewGuid());
        public List<Guid> Activated { get; } = [];
        public List<Guid> Restored { get; } = [];
        public bool ThrowOnRestore { get; set; }

        public Task<PowerPlanSnapshot> CaptureActiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Active);
        }

        public Task ActivateAsync(
            Guid powerPlanId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Activated.Add(powerPlanId);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(
            PowerPlanSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRestore)
            {
                throw new InvalidOperationException("injected restore failure");
            }

            Restored.Add(snapshot.PowerPlanId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecovery : ISystemSupportRecoveryStore
    {
        public List<SystemSupportRecoveryEntry> Entries { get; } = [];

        public Task SaveAsync(
            SystemSupportRecoveryEntry entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid recoveryId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.RemoveAll(item => item.RecoveryId == recoveryId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemSupportRecoveryEntry>> GetPendingAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SystemSupportRecoveryEntry>>(
                Entries.ToArray());
        }
    }

    private sealed class FakeAudit : ISystemSupportAuditSink
    {
        public List<SystemSupportAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            SystemSupportAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
