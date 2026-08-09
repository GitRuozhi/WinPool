using WinPool.Application;

namespace WinPool.Testing.Tools.Tests;

public sealed class ElevatedBrokerContractTests
{
    [Fact]
    public void ValidatorAcceptsOnlyThePayloadForTheSelectedOperation()
    {
        var now = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid();
        var session = Guid.NewGuid();
        const int agentProcessId = 123;
        const string userHash = "user-hash";
        var candidate = new TemporaryCleanupCandidate(
            new TemporaryCleanupCandidateId("candidate"),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "candidate.tmp")),
            TemporaryFileScope.CurrentUserTemporaryFiles,
            10,
            false,
            false);
        var request = new ElevatedBrokerExecutionRequest(
            nonce,
            session,
            agentProcessId,
            userHash,
            "plan-hash",
            now.AddMinutes(1),
            ElevatedBrokerOperationKind.CleanTemporaryFiles,
            [candidate]);

        Assert.Null(
            ElevatedBrokerExecutionValidator.Validate(
                request,
                nonce,
                session,
                agentProcessId,
                userHash,
                now));
        Assert.Equal(
            "broker.request.temporary-cleanup-invalid",
            ElevatedBrokerExecutionValidator.Validate(
                request with { PowerPlanId = Guid.NewGuid() },
                nonce,
                session,
                agentProcessId,
                userHash,
                now));
        Assert.Equal(
            "broker.request.nonce-mismatch",
            ElevatedBrokerExecutionValidator.Validate(
                request,
                Guid.NewGuid(),
                session,
                agentProcessId,
                userHash,
                now));
        Assert.Equal(
            "broker.request.temporary-cleanup-invalid",
            ElevatedBrokerExecutionValidator.Validate(
                request with
                {
                    TemporaryCleanupCandidates = Enumerable
                        .Repeat(
                            candidate,
                            ElevatedBrokerExecutionValidator
                                .MaximumTemporaryCleanupCandidates + 1)
                        .ToArray()
                },
                nonce,
                session,
                agentProcessId,
                userHash,
                now));
    }

    [Fact]
    public void ValidatorRejectsFreeFormOrUntrustedRamMapIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid();
        var session = Guid.NewGuid();
        var request = new ElevatedBrokerExecutionRequest(
            nonce,
            session,
            123,
            "user-hash",
            "plan-hash",
            now.AddMinutes(1),
            ElevatedBrokerOperationKind.ClearSystemFileCache,
            RamMapMode: RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
            PlannedRamMapIdentity: new RamMapToolIdentity(
                "path-binding",
                "1.63",
                "Microsoft Corporation",
                new string('a', 64),
                SignatureTrusted: false));

        Assert.Equal(
            "broker.request.rammap-identity-invalid",
            ElevatedBrokerExecutionValidator.Validate(
                request,
                nonce,
                session,
                123,
                "user-hash",
                now));
        Assert.Null(
            ElevatedBrokerExecutionValidator.Validate(
                request with
                {
                    PlannedRamMapIdentity =
                        request.PlannedRamMapIdentity! with { SignatureTrusted = true }
                },
                nonce,
                session,
                123,
                "user-hash",
                now));
    }

    [Fact]
    public async Task DispatcherRechecksTemporaryPathPolicyInsideBroker()
    {
        var fixture = new DispatcherFixture();
        var candidate = new TemporaryCleanupCandidate(
            new TemporaryCleanupCandidateId("candidate"),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "candidate.tmp")),
            TemporaryFileScope.CurrentUserTemporaryFiles,
            10,
            false,
            false);
        fixture.PathPolicy.Allow = false;
        var result = await fixture.Dispatcher.ExecuteAsync(
            fixture.NewRequest(
                ElevatedBrokerOperationKind.CleanTemporaryFiles,
                temporaryCleanupCandidates: [candidate]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("broker.temporary-cleanup.policy-rejected", result.Code);
        Assert.Equal(0, fixture.TemporaryFiles.CleanCount);
    }

    [Fact]
    public async Task DispatcherRechecksVolumeIdentityBeforeFixedOperation()
    {
        var fixture = new DispatcherFixture();
        var volume = fixture.Volume.Current!;
        var changed = volume with { StableIdentity = volume.StableIdentity + "-changed" };
        var rejected = await fixture.Dispatcher.ExecuteAsync(
            fixture.NewRequest(
                ElevatedBrokerOperationKind.FlushVolume,
                volumeTarget: changed),
            CancellationToken.None);
        var completed = await fixture.Dispatcher.ExecuteAsync(
            fixture.NewRequest(
                ElevatedBrokerOperationKind.TrimOrOptimizeVolume,
                volumeTarget: volume),
            CancellationToken.None);

        Assert.False(rejected.Succeeded);
        Assert.Equal(0, fixture.Volume.FlushCount);
        Assert.True(completed.Succeeded);
        Assert.Equal(1, fixture.Volume.OptimizeCount);
    }

    [Fact]
    public async Task MsiInstallAcceptsOnlyHashNamedFioStagingPath()
    {
        var fixture = new DispatcherFixture();
        var hash = new string('a', 64);
        var request = fixture.NewRequest(ElevatedBrokerOperationKind.InstallMsiTool) with
        {
            MsiToolInstall = new MsiToolInstallSnapshot(
                new ToolId("fio"),
                Path.Combine("tool-downloads", $"{hash}.msi"),
                hash)
        };

        var completed = await fixture.Dispatcher.ExecuteAsync(request, CancellationToken.None);
        var escaped = ElevatedBrokerExecutionValidator.Validate(
            request with
            {
                MsiToolInstall = request.MsiToolInstall with
                {
                    PackageRelativePath = Path.Combine("..", "arbitrary.msi")
                }
            },
            fixture.Nonce,
            fixture.Session,
            123,
            "user-hash",
            DateTimeOffset.UtcNow);

        Assert.True(completed.Succeeded);
        Assert.Equal(1, fixture.MsiInstaller.InstallCount);
        Assert.Equal("broker.request.msi-install-invalid", escaped);
    }

    private sealed class DispatcherFixture
    {
        public DispatcherFixture()
        {
            Nonce = Guid.NewGuid();
            Session = Guid.NewGuid();
            TemporaryFiles = new FakeTemporaryFiles();
            PathPolicy = new FakePathPolicy();
            Volume = new FakeVolume();
            MsiInstaller = new FakeMsiInstaller();
            Dispatcher = new ElevatedBrokerDispatcher(
                new ElevatedBrokerExecutionPorts(
                    TemporaryFiles,
                    PathPolicy,
                    new FakeRamMap(),
                    Volume,
                    new FakePowerPlan(),
                    MsiInstaller),
                Nonce,
                Session,
                123,
                "user-hash");
        }

        public Guid Nonce { get; }
        public Guid Session { get; }
        public FakeTemporaryFiles TemporaryFiles { get; }
        public FakePathPolicy PathPolicy { get; }
        public FakeVolume Volume { get; }
        public FakeMsiInstaller MsiInstaller { get; }
        public ElevatedBrokerDispatcher Dispatcher { get; }

        public ElevatedBrokerExecutionRequest NewRequest(
            ElevatedBrokerOperationKind operation,
            IReadOnlyList<TemporaryCleanupCandidate>? temporaryCleanupCandidates = null,
            VolumeTargetSnapshot? volumeTarget = null) =>
            new(
                Nonce,
                Session,
                123,
                "user-hash",
                "plan",
                DateTimeOffset.UtcNow.AddMinutes(1),
                operation,
                temporaryCleanupCandidates,
                volumeTarget);
    }

    private sealed class FakeTemporaryFiles : ITemporaryFileCleanupPort
    {
        public int CleanCount { get; private set; }

        public Task<IReadOnlyList<TemporaryCleanupCandidate>> ScanAsync(
            IReadOnlyList<TemporaryFileScope> scopes,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TemporaryCleanupCandidate>>([]);

        public Task<TemporaryCleanupPortResult> CleanAsync(
            IReadOnlyList<TemporaryCleanupCandidate> approvedCandidates,
            CancellationToken cancellationToken)
        {
            CleanCount++;
            return Task.FromResult(
                new TemporaryCleanupPortResult(
                    approvedCandidates
                        .Select(candidate => new TemporaryCleanupItemResult(
                            candidate.Id,
                            TemporaryCleanupItemStatus.Removed,
                            "removed"))
                        .ToArray()));
        }
    }

    private sealed class FakePathPolicy : ITemporaryCleanupPathPolicy
    {
        public bool Allow { get; set; } = true;

        public TemporaryCleanupCandidateDecision Evaluate(
            TemporaryCleanupCandidate candidate) =>
            new(candidate, Allow, Allow ? "allowed" : "denied");
    }

    private sealed class FakeRamMap : IRamMapCacheClearPort
    {
        public bool SupportsElevatedBroker => true;

        public Task<RamMapToolIdentity?> DetectIdentityAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<RamMapToolIdentity?>(null);

        public Task<RamMapCacheClearEvidence> ClearAsync(
            RamMapCacheClearRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeVolume : IVolumeMaintenancePort
    {
        public FakeVolume()
        {
            var system = WinPool.Domain.SystemId.New();
            Current = new VolumeTargetSnapshot(
                new WinPool.Domain.StorageObjectId(
                    system,
                    WinPool.Domain.StorageObjectKind.Partition,
                    "volume"),
                "volume-guid",
                "T:\\");
        }

        public VolumeTargetSnapshot? Current { get; }
        public int FlushCount { get; private set; }
        public int OptimizeCount { get; private set; }

        public Task<VolumeTargetSnapshot?> ResolvePlannedTargetAsync(
            WinPool.Domain.StorageObjectId volumeId,
            string planHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task<VolumeTargetSnapshot?> ResolveCurrentTargetAsync(
            WinPool.Domain.StorageObjectId volumeId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task<VolumeMaintenanceEvidence> FlushAsync(
            VolumeTargetSnapshot target,
            CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.FromResult(new VolumeMaintenanceEvidence("flush", string.Empty));
        }

        public Task<VolumeMaintenanceEvidence> TrimOrOptimizeAsync(
            VolumeTargetSnapshot target,
            CancellationToken cancellationToken)
        {
            OptimizeCount++;
            return Task.FromResult(new VolumeMaintenanceEvidence("trim", string.Empty));
        }
    }

    private sealed class FakePowerPlan : ITemporaryPowerPlanPort
    {
        public Task<PowerPlanSnapshot> CaptureActiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new PowerPlanSnapshot(Guid.NewGuid()));

        public Task ActivateAsync(Guid powerPlanId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreAsync(
            PowerPlanSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeMsiInstaller : IMsiToolInstallPort
    {
        public int InstallCount { get; private set; }

        public Task<MsiToolInstallEvidence> InstallAsync(
            MsiToolInstallSnapshot package,
            CancellationToken cancellationToken)
        {
            InstallCount++;
            return Task.FromResult(new MsiToolInstallEvidence(0, false));
        }
    }
}
