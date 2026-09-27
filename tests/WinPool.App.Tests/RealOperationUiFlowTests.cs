using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealOperationUiFlowTests
{
    [Fact]
    public void DataLocationSwitchNeedsExplicitCompletedShutdownReceipt()
    {
        var correlation = CorrelationId.New();
        var completed = new ShutdownResponse(new ShutdownResult(true, [], 0, true));
        Assert.True(AgentShutdownReceiptPolicy.AllowsDataLocationSwitch(
            ApplicationResult<AgentResponse>.Succeeded(completed, correlation)));
        Assert.False(AgentShutdownReceiptPolicy.AllowsDataLocationSwitch(
            ApplicationResult<AgentResponse>.Succeeded(
                new ShutdownResponse(new ShutdownResult(false, [], 0, true)), correlation)));
        Assert.False(AgentShutdownReceiptPolicy.AllowsDataLocationSwitch(
            ApplicationResult<AgentResponse>.Succeeded(
                new AgentAcknowledgement(), correlation)));
        Assert.False(AgentShutdownReceiptPolicy.AllowsDataLocationSwitch(
            ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.OutcomeUnknown, correlation)));
    }

    [Fact]
    public async Task RealModeNeedsPositiveAgentReplyAndDisarmsBeforeExitReply()
    {
        var connection = new RecordingConnection(request => request switch
        {
            EnterAgentRealModeRequest => ApplicationResult<AgentResponse>.Succeeded(
                new AgentRealModeResponse(false, "not_ready"), request.CorrelationId),
            ExitAgentRealModeRequest => ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.OutcomeUnknown, request.CorrelationId),
            _ => throw new InvalidOperationException()
        });
        var mode = new AgentRealModeSession(connection);
        Assert.NotNull(await mode.EnterAsync(CancellationToken.None));
        Assert.False(mode.IsArmed);

        connection.Reply = request => request switch
        {
            EnterAgentRealModeRequest => ApplicationResult<AgentResponse>.Succeeded(
                new AgentRealModeResponse(true, "armed"), request.CorrelationId),
            ExitAgentRealModeRequest =>
                AssertDisarmedAtExit(request, mode),
            _ => throw new InvalidOperationException()
        };
        Assert.Null(await mode.EnterAsync(CancellationToken.None));
        Assert.True(mode.IsArmed);
        Assert.NotNull(await mode.ExitAsync(CancellationToken.None));
        Assert.False(mode.IsArmed);
        Assert.Equal(3, connection.Requests.Count);
        Assert.Equal(mode.ProductSessionId,
            Assert.IsType<ExitAgentRealModeRequest>(connection.Requests[^1]).ProductSessionId);
    }

    [Fact]
    public async Task LostPrepareReplyReusesPreparationIdAndAcceptNeedsExplicitConfirmation()
    {
        var (proposal, plan) = CreatePlan();
        var prepareCalls = 0;
        var connection = new RecordingConnection(request => request switch
        {
            PrepareAgentRealOperationRequest => ++prepareCalls == 1
                ? ApplicationResult<AgentResponse>.FromStatus(
                    ApplicationStatus.OutcomeUnknown, request.CorrelationId)
                : ApplicationResult<AgentResponse>.Succeeded(
                    new AgentRealOperationResponse(plan, RealOperationState.Prepared,
                        [], null, false), request.CorrelationId),
            AcceptAgentRealOperationRequest => ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.OutcomeUnknown, request.CorrelationId),
            QueryAgentRealOperationRequest => ApplicationResult<AgentResponse>.Succeeded(
                new AgentRealOperationResponse(plan, RealOperationState.Succeeded,
                    [], null, false), request.CorrelationId),
            _ => throw new InvalidOperationException()
        });
        var flow = new RealOperationRequestSession(connection, "product-session");
        var prepared = await flow.PrepareAsync(proposal, CancellationToken.None);
        var frozen = Assert.IsType<AgentRealOperationResponse>(prepared.Value);
        Assert.Equal(2, prepareCalls);
        Assert.All(connection.Requests.OfType<PrepareAgentRealOperationRequest>(),
            request => Assert.Equal(flow.PreparationId, request.PreparationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flow.AcceptOnceAsync(CancellationToken.None));

        flow.Confirm(frozen);
        var accept = await flow.AcceptOnceAsync(CancellationToken.None);
        Assert.Equal(ApplicationStatus.OutcomeUnknown, accept.Status);
        var query = await flow.QueryAsync(CancellationToken.None);
        Assert.Equal(RealOperationState.Succeeded,
            Assert.IsType<AgentRealOperationResponse>(query.Value).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flow.AcceptOnceAsync(CancellationToken.None));
        Assert.Single(connection.Requests.OfType<AcceptAgentRealOperationRequest>());
        Assert.Equal(plan.OperationId,
            Assert.Single(connection.Requests.OfType<QueryAgentRealOperationRequest>()).OperationId);
    }

    [Fact]
    public void UnformattedBasicDataOmitsFormatAndValidatedFormattedPlanKeepsDependencies()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk-id");
        var unformatted = RealOperationProposalFactory.CreatePartition(
            system, disk, RealPartitionRole.BasicData,
            1024 * 1024, 1024L * 1024 * 1024,
            null, 65536, false, null, null);
        Assert.Single(unformatted.Steps);
        Assert.IsType<CreatePartitionCommand>(unformatted.Steps[0].Command);

        var formatted = RealOperationProposalFactory.CreatePartition(
            system, disk, RealPartitionRole.BasicData,
            1024 * 1024, 1024L * 1024 * 1024,
            RealFileSystem.Ntfs, 65536, false, "Data", 'D');
        Assert.Equal(3, formatted.Steps.Count);
        Assert.Equal(["create-partition", "format-volume"],
            formatted.Steps[2].DependsOn);
        Assert.IsType<SetDriveLetterCommand>(formatted.Steps[2].Command);
        Assert.Throws<ArgumentException>(() =>
            RealOperationProposalFactory.CreatePartition(
                system, disk, RealPartitionRole.Efi,
                1024 * 1024, 256L * 1024 * 1024,
                RealFileSystem.Fat32, 65536, false, null, null));
    }

    [Fact]
    public void FirstVirtualDiskFreezesSimpleFixedAndOrderedOptionalPartitionSteps()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(
            system, pool, new RealOperationProposalFactory.VirtualDiskOptions(
                "Data", 16L * 1024 * 1024 * 1024,
                true, true, true, "WinPool_Test", 'E'));
        Assert.Equal(OperationIntent.CreateVirtualDisk, proposal.Intent);
        Assert.Equal(6, proposal.Steps.Count);
        var create = Assert.IsType<CreateVirtualDiskCommand>(proposal.Steps[0].Command);
        Assert.Equal(65536, create.InterleaveBytes);
        Assert.Equal(1, create.DataColumns);
        Assert.Equal(16L * 1024 * 1024 * 1024, create.SizeBytes);
        Assert.Equal(RealPartitionRole.Msr,
            Assert.IsType<CreatePartitionCommand>(proposal.Steps[2].Command).Role);
        Assert.Equal(RealPartitionRole.BasicData,
            Assert.IsType<CreatePartitionCommand>(proposal.Steps[3].Command).Role);
        Assert.Contains("create-data", proposal.Steps[5].DependsOn);
        Assert.Contains("format-data", proposal.Steps[5].DependsOn);

        var noPartition = RealOperationProposalFactory.CreateFirstVirtualDisk(
            system, pool, new RealOperationProposalFactory.VirtualDiskOptions(
                "Raw", 16L * 1024 * 1024 * 1024,
                false, false, false, null, null));
        Assert.Single(noPartition.Steps);
    }

    [Fact]
    public void CreatePoolWithoutAutomaticVirtualDiskHasOnlyTheExactPoolStep()
    {
        var system = SystemId.New();
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk,
            "selected-physical-id");
        var proposal = RealOperationProposalFactory.CreateSingleMemberPool(
            system, physical, "NewPool", null);

        Assert.Equal(OperationIntent.CreateStoragePool, proposal.Intent);
        Assert.Equal(physical, Assert.Single(proposal.Targets));
        var step = Assert.Single(proposal.Steps);
        Assert.Equal("create-pool", step.Id);
        Assert.Empty(step.DependsOn);
        var command = Assert.IsType<CreatePoolCommand>(step.Command);
        Assert.Equal(physical, command.PhysicalDisk.Existing);
        Assert.Equal("NewPool", command.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticPoolVirtualDiskFreezesOptionsAndDependsOnCreatedPool(
        bool autoCreatePartition)
    {
        var system = SystemId.New();
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk,
            "selected-physical-id");
        var options = new RealOperationProposalFactory.VirtualDiskOptions(
            "ExactVD", 32L * 1024 * 1024 * 1024,
            autoCreatePartition, autoCreatePartition, autoCreatePartition,
            autoCreatePartition ? "ExactLabel" : null,
            autoCreatePartition ? 'E' : null);
        var proposal = RealOperationProposalFactory.CreateSingleMemberPool(
            system, physical, "ExactPool", options);

        Assert.Equal(autoCreatePartition ? 7 : 2, proposal.Steps.Count);
        var pool = Assert.IsType<CreatePoolCommand>(proposal.Steps[0].Command);
        Assert.Equal("ExactPool", pool.Name);
        var create = Assert.IsType<CreateVirtualDiskCommand>(proposal.Steps[1].Command);
        Assert.Equal("create-pool", create.Pool.CreatedByStep);
        Assert.Equal(["create-pool"], proposal.Steps[1].DependsOn);
        Assert.Equal("ExactVD", create.Name);
        Assert.Equal(32L * 1024 * 1024 * 1024, create.SizeBytes);
        Assert.Equal(65536, create.InterleaveBytes);
        Assert.Equal(1, create.DataColumns);
        if (!autoCreatePartition)
            return;

        Assert.Equal("create-vdisk", proposal.Steps[2].DependsOn[0]);
        var msr = Assert.IsType<CreatePartitionCommand>(proposal.Steps[3].Command);
        Assert.Equal(RealPartitionRole.Msr, msr.Role);
        var data = Assert.IsType<CreatePartitionCommand>(proposal.Steps[4].Command);
        Assert.Equal(RealPartitionRole.BasicData, data.Role);
        var format = Assert.IsType<FormatVolumeCommand>(proposal.Steps[5].Command);
        Assert.Equal(RealFileSystem.Ntfs, format.FileSystem);
        Assert.Equal(65536, format.ClusterBytes);
        Assert.Equal("ExactLabel", format.Label);
        var letter = Assert.IsType<SetDriveLetterCommand>(proposal.Steps[6].Command);
        Assert.Equal('E', letter.NewLetter);
    }

    [Fact]
    public void StandaloneClearTargetsOneExistingOsDiskAndFixedRawState()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "wdc-exact-id");
        var proposal = RealOperationProposalFactory.ClearToRaw(system, disk);
        Assert.Equal(OperationIntent.ClearDisk, proposal.Intent);
        Assert.Equal(RealOperationValidator.ClearDiskExpectedFinalState,
            proposal.ExpectedFinalState);
        Assert.Equal(disk, Assert.Single(proposal.Targets));
        var clear = Assert.IsType<ClearDiskCommand>(
            Assert.Single(proposal.Steps).Command);
        Assert.False(clear.RemoveOem);
        Assert.Equal(disk, clear.Disk.Existing);
    }

    [Fact]
    public void GptInitializationHonorsMsrPreferenceAndRealRefsRemainsDisabled()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk-id");
        var plain = RealOperationProposalFactory.InitializeGpt(system, disk, false);
        Assert.Single(plain.Steps);
        var withMsr = RealOperationProposalFactory.InitializeGpt(system, disk, true);
        Assert.Equal(2, withMsr.Steps.Count);
        var msr = Assert.IsType<CreatePartitionCommand>(withMsr.Steps[1].Command);
        Assert.Equal(RealPartitionRole.Msr, msr.Role);
        Assert.Equal(1024L * 1024, msr.OffsetBytes);
        Assert.Equal(16L * 1024 * 1024, msr.SizeBytes);
        Assert.Equal(["initialize-gpt"], withMsr.Steps[1].DependsOn);
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.CreatePartition(
            system, disk, RealPartitionRole.BasicData, 1024 * 1024,
            1024L * 1024 * 1024, RealFileSystem.ReFs, 65536,
            false, "Data", null));
    }

    [Fact]
    public void ExistingRealFormatRejectsRefsAndUnknownWithoutExFatFallback()
    {
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition,
            "partition-id");
        Assert.Null(RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "ReFS", 65536, false, "Data"));
        Assert.Null(RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "unknown", 65536, false, "Data"));
        var ntfs = RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "NTFS", 65536, false, "Data");
        Assert.Equal(RealFileSystem.Ntfs,
            Assert.IsType<FormatVolumeCommand>(Assert.Single(ntfs!.Steps).Command).FileSystem);
        var exfat = RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "exFAT", 65536, false, "Data");
        Assert.Equal(RealFileSystem.ExFat,
            Assert.IsType<FormatVolumeCommand>(Assert.Single(exfat!.Steps).Command).FileSystem);
    }

    [Fact]
    public void FrozenConfirmationShowsExactTargetsTrustedFactsParametersAndLoss()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os-disk-0-id");
        var clear = RealOperationProposalFactory.ClearToRaw(system, disk);
        clear = clear with { Steps = [clear.Steps[0] with
        {
            BeforeCondition = "WDC WD40EZAZ serial ...FP80; OS disk number 0; PhysicalDisk id physical-unique",
            DataLoss = "partition 1 offset 17408 size 16759808; partition 2 E: NTFS offset 17825792"
        }] };
        var text = RealOperationConfirmationFormatter.Format(Freeze(clear), true);
        Assert.Contains("os-disk-0-id", text);
        Assert.Contains("WDC WD40EZAZ", text);
        Assert.Contains("OS disk number 0", text);
        Assert.Contains("offset 17825792", text);
        Assert.Contains("removeOem=False", text);
        Assert.Contains("数据损失", text);

        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var create = RealOperationProposalFactory.CreateFirstVirtualDisk(
            system, pool, new RealOperationProposalFactory.VirtualDiskOptions(
                "NewVD", 16L * 1024 * 1024 * 1024,
                true, true, true, "Data", 'E'));
        var createText = RealOperationConfirmationFormatter.Format(Freeze(create), false);
        Assert.Contains("pool-id", createText);
        Assert.Contains("sizeBytes=17179869184", createText);
        Assert.Contains("interleaveBytes=65536 dataColumns=1", createText);
        Assert.Contains("role=Msr offsetBytes=1048576 sizeBytes=16777216", createText);
        Assert.Contains("fileSystem=Ntfs clusterBytes=65536 full=False", createText);
        Assert.Contains("createdByStep=", createText);
    }

    [Fact]
    public async Task StopCancellationMakesNoStopRequestAndConfirmedStopUsesQueriedPlanHash()
    {
        var (_, plan) = CreatePlan();
        var observed = new AgentRealOperationResponse(
            plan, RealOperationState.Running, [], null, false);
        var connection = new RecordingConnection(request => request switch
        {
            QueryAgentRealOperationRequest => ApplicationResult<AgentResponse>.Succeeded(
                observed, request.CorrelationId),
            StopAgentRealOperationFollowingStepsRequest => ApplicationResult<AgentResponse>.Succeeded(
                observed, request.CorrelationId),
            _ => throw new InvalidOperationException()
        });
        var flow = new RealOperationStopSession(connection, "product-session");
        var queried = await flow.QueryAsync(plan.OperationId, CancellationToken.None);
        var frozen = Assert.IsType<AgentRealOperationResponse>(queried.Value);
        Assert.Null(flow.StopAfterCurrentStepAsync(frozen, false, CancellationToken.None));
        Assert.Empty(connection.Requests.OfType<StopAgentRealOperationFollowingStepsRequest>());
        var result = await flow.StopAfterCurrentStepAsync(frozen, true, CancellationToken.None)!;
        Assert.True(result.IsSuccess);
        var stop = Assert.Single(connection.Requests.OfType<StopAgentRealOperationFollowingStepsRequest>());
        Assert.Equal(plan.OperationId, stop.OperationId);
        Assert.Equal(plan.PlanHash, stop.PlanHash);
        Assert.Equal("product-session", stop.ProductSessionId);
    }

    [Fact]
    public void RealResizeUiUsesAgentAllowedRangeAndDoesNotUseSimulatedLimits()
    {
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition,
            "partition-id");
        var range = new RealPartitionResizeRange(partition,
            1024L * 1024 * 1024,
            256L * 1024 * 1024, 8L * 1024 * 1024 * 1024,
            768L * 1024 * 1024, 2L * 1024 * 1024 * 1024,
            "fresh-fingerprint", DateTimeOffset.UtcNow, "real.resize_range_verified");
        Assert.True(RealPartitionResizeUiRange.TryGetWholeMibTargets(
            range, true, out var extendMin, out var extendMax));
        Assert.Equal(1025, extendMin);
        Assert.Equal(2048, extendMax);
        Assert.True(RealPartitionResizeUiRange.TryGetWholeMibTargets(
            range, false, out var shrinkMin, out var shrinkMax));
        Assert.Equal(768, shrinkMin);
        Assert.Equal(1023, shrinkMax);
        Assert.True(RealPartitionResizeUiRange.IsSupportedFileSystem("RAW"));
        Assert.True(RealPartitionResizeUiRange.IsSupportedFileSystem("NTFS"));
        Assert.False(RealPartitionResizeUiRange.IsSupportedFileSystem("ReFS"));
        Assert.False(RealPartitionResizeUiRange.IsSupportedFileSystem("exFAT"));
    }

    [Fact]
    public void RebuildListsExactRemovalBeforeReplacementAndKeepsOnePhysicalMember()
    {
        var system = SystemId.New();
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "member-id");
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "old-pool-id");
        var disk = new StorageObjectId(system, StorageObjectKind.VirtualDisk, "old-vdisk-id");
        var proposal = RealOperationProposalFactory.RebuildSingleMemberPool(
            system, physical, pool, disk, "NewPool",
            new RealOperationProposalFactory.VirtualDiskOptions(
                "NewDisk", 16L * 1024 * 1024 * 1024,
                true, true, true, "Data", 'E'));

        Assert.Equal(OperationIntent.RebuildStoragePool, proposal.Intent);
        Assert.Equal([physical, pool, disk], proposal.Targets);
        Assert.Collection(proposal.Steps.Take(4),
            step => Assert.IsType<DeleteVirtualDiskCommand>(step.Command),
            step => Assert.IsType<DeletePoolCommand>(step.Command),
            step => Assert.IsType<CreatePoolCommand>(step.Command),
            step => Assert.IsType<CreateVirtualDiskCommand>(step.Command));
        Assert.Equal(["delete-vdisk"], proposal.Steps[1].DependsOn);
        Assert.Equal(["delete-pool"], proposal.Steps[2].DependsOn);
        Assert.Equal(["create-pool"], proposal.Steps[3].DependsOn);
        Assert.Equal("create-pool",
            Assert.IsType<CreateVirtualDiskCommand>(proposal.Steps[3].Command)
                .Pool.CreatedByStep);
    }

    private static ApplicationResult<AgentResponse> AssertDisarmedAtExit(
        AgentRequest request, AgentRealModeSession mode)
    {
        Assert.False(mode.IsArmed);
        return ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.OutcomeUnknown, request.CorrelationId);
    }

    private static (RealOperationIntentRequest, OperationPlan) CreatePlan()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk-id");
        var proposal = RealOperationProposalFactory.OneStep(system,
            OperationIntent.InitializeDisk, disk,
            new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
            "GPT", "Existing partition data is lost");
        return (proposal, Freeze(proposal));
    }

    private static OperationPlan Freeze(RealOperationIntentRequest proposal)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product-session",
            Guid.NewGuid().ToString("D"), 42, now,
            Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(),
            EnvironmentKind.LocalMachine, "machine-binding",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
            false, now);
        var plan = RealOperationPlanFactory.Create(proposal,
            OperationId.New(), environment, session, "inventory",
            "target-fingerprint", "physical-fingerprint", "support", now,
            now.AddMinutes(2));
        return plan;
    }

    private sealed class RecordingConnection(
        Func<AgentRequest, ApplicationResult<AgentResponse>> reply) : IAgentConnection
    {
        public Func<AgentRequest, ApplicationResult<AgentResponse>> Reply { get; set; } = reply;
        public List<AgentRequest> Requests { get; } = [];

        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<AgentEvent> WatchAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApplicationResult<AgentResponse>> SendAsync(
            AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Reply(request));
        }
    }
}
