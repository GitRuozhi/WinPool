using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealOperationUiFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticInitializedDiskLayoutUsesTheSameRealMaximumGeometry(bool createMsr)
    {
        const long size = 32L << 30;
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "exact-created-disk");
        var proposal = RealOperationProposalFactory.ConfigureInitializedDisk(system, disk, null,
            createMsr, size, formatNtfs: true, label: "WinPool_Test", letter: 'E');
        Assert.NotNull(proposal);
        RealOperationValidator.Validate(proposal);
        var data = Assert.Single(proposal.Steps.Select(step => step.Command)
            .OfType<CreatePartitionCommand>(), command => command.Role == RealPartitionRole.BasicData);
        var offset = (createMsr ? 17L : 1L) << 20;
        var geometry = EditWorkspace.GetRealPartitionCreateGeometry(size, offset, size - offset);
        Assert.Equal(offset, data.OffsetBytes);
        Assert.Equal(geometry.MaximumSizeBytes, data.SizeBytes);
        Assert.Equal(size - (1L << 20), data.OffsetBytes + data.SizeBytes);
        Assert.Single(proposal.Steps.Select(step => step.Command).OfType<FormatVolumeCommand>());
        Assert.Single(proposal.Steps.Select(step => step.Command).OfType<SetDriveLetterCommand>());
    }

    [Fact]
    public void PreparationReservationPreventsAnotherSubmitAndReleasesBeforeAccept()
    {
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        Assert.True(state.IsBlocked);
        Assert.False(state.TryReservePreparation());
        state.ReleasePreparation(state.ReservationId);
        Assert.False(state.IsBlocked);
        Assert.True(state.TryReservePreparation());
    }

    [Theory]
    [InlineData(RealOperationState.Prepared, false)]
    [InlineData(RealOperationState.Accepted, false)]
    [InlineData(RealOperationState.Running, false)]
    [InlineData(RealOperationState.OutcomeUnknown, false)]
    [InlineData(RealOperationState.Failed, true)]
    [InlineData(RealOperationState.PartiallyCompleted, true)]
    [InlineData(RealOperationState.Succeeded, true)]
    public void KnownOperationKeepsBarrierUntilMatchingReconciledTerminalStatus(
        RealOperationState observedState, bool requiresReconciliation)
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        state.TrackPrepared(new AgentRealOperationResponse(plan, RealOperationState.Prepared, [], null, false));
        // Polling timeout, transport failure, editor navigation and mode changes
        // do not produce an authoritative status and cannot release this state.
        state.ReleasePreparation(state.ReservationId);
        Assert.True(state.Observe(new AgentRealOperationResponse(plan, observedState, [], null, requiresReconciliation)));
        Assert.True(state.IsBlocked);
        Assert.Equal(plan.OperationId, state.OperationId);
        Assert.False(state.TryReservePreparation());

        var (_, otherPlan) = CreatePlan();
        Assert.False(state.Observe(new AgentRealOperationResponse(otherPlan, RealOperationState.Succeeded, [], null, false)));
        Assert.False(state.Observe(new AgentRealOperationResponse(plan with { PlanHash = "different" },
            RealOperationState.Succeeded, [], null, false)));
        Assert.True(state.IsBlocked);
        Assert.True(state.Observe(new AgentRealOperationResponse(plan, RealOperationState.Cancelled, [], null, false)));
        Assert.False(state.IsBlocked);
        Assert.Null(state.OperationId);
        Assert.True(state.TryReservePreparation());
    }

    [Theory]
    [InlineData(RealOperationState.Succeeded)]
    [InlineData(RealOperationState.Rejected)]
    [InlineData(RealOperationState.Cancelled)]
    [InlineData(RealOperationState.Failed)]
    [InlineData(RealOperationState.PartiallyCompleted)]
    public void AllReconciledTerminalStatesReleaseTheExactBarrier(RealOperationState terminal)
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        state.TrackPrepared(new AgentRealOperationResponse(plan, RealOperationState.Prepared, [], null, false));
        state.Observe(new AgentRealOperationResponse(plan, terminal, [], null, false));
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void QueryAfterReconnectAdoptsUnfinishedOperationAndCannotClearAnotherIdentity()
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.Running, [], null, false));
        Assert.True(state.IsBlocked);
        Assert.Equal(plan.OperationId, state.OperationId);
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.Succeeded, [], null, false));
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void OldSubmissionFinallyCannotReleaseANewerPreparation()
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        var oldReservationId = state.ReservationId;
        state.TrackPrepared(new AgentRealOperationResponse(plan, RealOperationState.Prepared, [], null, false));
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.Succeeded, [], null, false));
        Assert.True(state.TryReservePreparation());
        state.ReleasePreparation(oldReservationId);
        Assert.True(state.IsBlocked);
        Assert.False(state.TryReservePreparation());
        state.ReleasePreparation(state.ReservationId);
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void QueryDuringPreparationBindsDiscoveredWriteAndPreservesItsIdentity()
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        var reservationId = state.ReservationId;
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.Running, [], null, false));
        state.ReleasePreparation(reservationId);
        Assert.True(state.IsBlocked);
        Assert.Equal(plan.OperationId, state.OperationId);
        var (_, otherPlan) = CreatePlan();
        Assert.False(state.TrackPrepared(new AgentRealOperationResponse(
            otherPlan, RealOperationState.Prepared, [], null, false)));
        Assert.False(state.Observe(new AgentRealOperationResponse(
            otherPlan, RealOperationState.Cancelled, [], null, false)));
        Assert.True(state.IsBlocked);
        Assert.Equal(plan.OperationId, state.OperationId);
    }

    [Fact]
    public async Task FinalConfirmationCancellationStopsExactPreparedPlanWithoutAccept()
    {
        var (_, plan) = CreatePlan();
        var prepared = new AgentRealOperationResponse(plan, RealOperationState.Prepared, [], null, false);
        var connection = new RecordingConnection(request => request switch
        {
            StopAgentRealOperationFollowingStepsRequest => ApplicationResult<AgentResponse>.Succeeded(
                prepared with { State = RealOperationState.Cancelled }, request.CorrelationId),
            _ => throw new InvalidOperationException("Cancellation must not accept a storage write.")
        });
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        state.TrackPrepared(prepared);
        var flow = new RealOperationStopSession(connection, "product-session");
        var result = await flow.StopAfterCurrentStepAsync(prepared, true, CancellationToken.None)!;
        state.Observe(Assert.IsType<AgentRealOperationResponse>(result.Value));
        var stop = Assert.IsType<StopAgentRealOperationFollowingStepsRequest>(Assert.Single(connection.Requests));
        Assert.Equal(plan.OperationId, stop.OperationId);
        Assert.Equal(plan.PlanHash, stop.PlanHash);
        Assert.False(state.IsBlocked);
    }

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
    public void FirstVirtualDiskInitializesGptThenDefersLayoutUntilFreshRead()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(
            system, pool, new RealOperationProposalFactory.VirtualDiskOptions(
                "Data", 16L * 1024 * 1024 * 1024,
                true, true, true, "WinPool_Test", 'E'));
        Assert.Equal(OperationIntent.CreateVirtualDisk, proposal.Intent);
        Assert.Equal(2, proposal.Steps.Count);
        var create = Assert.IsType<CreateVirtualDiskCommand>(proposal.Steps[0].Command);
        Assert.Equal(65536, create.InterleaveBytes);
        Assert.Equal(1, create.DataColumns);
        Assert.Equal(16L * 1024 * 1024 * 1024, create.SizeBytes);
        Assert.Equal("create-vdisk",
            Assert.IsType<InitializeGptCommand>(proposal.Steps[1].Command).Disk.CreatedByStep);
        Assert.Equal(["create-vdisk"], proposal.Steps[1].DependsOn);
        Assert.Contains("provider-created MSR", proposal.ExpectedFinalState);

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

        Assert.Equal(autoCreatePartition ? 3 : 2, proposal.Steps.Count);
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

        Assert.Equal("create-vdisk",
            Assert.IsType<InitializeGptCommand>(proposal.Steps[2].Command).Disk.CreatedByStep);
        Assert.Equal(["create-vdisk"], proposal.Steps[2].DependsOn);
        Assert.Contains("fresh read", proposal.ExpectedFinalState);
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
    public void GptInitializationIsOnlyTheFirstPhaseAndRealRefsCreationRemainsDisabled()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk-id");
        var initialization = RealOperationProposalFactory.InitializeGpt(system, disk);
        Assert.Single(initialization.Steps);
        Assert.IsType<InitializeGptCommand>(initialization.Steps[0].Command);
        Assert.Contains("separate plan", initialization.ExpectedFinalState);
        Assert.Null(RealOperationProposalFactory.ConfigureInitializedDisk(
            system, disk, null, false));
        var reconcileOnly = RealOperationProposalFactory.ConfigureInitializedDisk(
            system, disk,
            new StorageObjectId(system, StorageObjectKind.Partition, "provider-msr-id"),
            false)!;
        Assert.IsType<DeletePartitionCommand>(Assert.Single(reconcileOnly.Steps).Command);
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.CreatePartition(
            system, disk, RealPartitionRole.BasicData, 1024 * 1024,
            1024L * 1024 * 1024, RealFileSystem.ReFs, 65536,
            false, "Data", null));
    }

    [Fact]
    public void ExistingRealFormatAllowsOnlyQuick64KiBRefsAndRejectsUnknown()
    {
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition,
            "partition-id");
        Assert.Null(RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "ReFS", 4096, false, "Data"));
        Assert.Null(RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "ReFS", 65536, true, "Data"));
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
        var refs = RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "ReFS", 65536, false, "Data");
        Assert.Equal(RealFileSystem.ReFs,
            Assert.IsType<FormatVolumeCommand>(Assert.Single(refs!.Steps).Command).FileSystem);
    }

    [Fact]
    public void RealHddTierActionsUseExactPoolAndTierTargets()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var tier = new StorageObjectId(system, StorageObjectKind.StorageTier, "tier-id");
        var createTier = RealOperationProposalFactory.CreateHddTierTemplate(
            system, pool, "HDDTemplate");
        Assert.Equal(OperationIntent.CreateStorageTier, createTier.Intent);
        Assert.Equal(pool, Assert.Single(createTier.Targets));
        var createTierCommand = Assert.IsType<CreateTierCommand>(
            Assert.Single(createTier.Steps).Command);
        Assert.Equal(pool, createTierCommand.Pool.Existing);
        Assert.Equal("HDDTemplate", createTierCommand.Name);
        Assert.Equal(65536, createTierCommand.InterleaveBytes);
        Assert.Equal(1, createTierCommand.DataColumns);

        var createVdisk = RealOperationProposalFactory.CreateTieredVirtualDisk(
            system, pool, tier, "HDDData", 16L * 1024 * 1024 * 1024);
        Assert.Equal(OperationIntent.CreateVirtualDisk, createVdisk.Intent);
        Assert.Equal([pool, tier], createVdisk.Targets);
        var createVdiskCommand = Assert.IsType<CreateTieredVirtualDiskCommand>(
            Assert.Single(createVdisk.Steps).Command);
        Assert.Equal(pool, createVdiskCommand.Pool.Existing);
        Assert.Equal(tier, createVdiskCommand.Tier.Existing);
        Assert.Equal(16L * 1024 * 1024 * 1024, createVdiskCommand.SizeBytes);

        var rename = RealOperationProposalFactory.RenameHddTier(system, tier, "Renamed");
        Assert.Equal(OperationIntent.RenameStorageObject, rename.Intent);
        Assert.Equal(tier, Assert.Single(rename.Targets));
        Assert.Equal("Renamed", Assert.IsType<RenameTierCommand>(
            Assert.Single(rename.Steps).Command).Name);
        var delete = RealOperationProposalFactory.DeleteHddTierTemplate(system, tier);
        Assert.Equal(OperationIntent.DeleteStorageTier, delete.Intent);
        Assert.Equal(tier, Assert.Single(delete.Targets));
        Assert.IsType<DeleteTierCommand>(Assert.Single(delete.Steps).Command);
    }

    [Fact]
    public void DissolveExplicitlyDeletesVirtualDiskThenPoolTierTemplateThenPool()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var vdisk = new StorageObjectId(system, StorageObjectKind.VirtualDisk, "vdisk-id");
        var template = new StorageObjectId(system, StorageObjectKind.StorageTier, "template-id");
        var proposal = RealOperationProposalFactory.DissolveSingleMemberPool(
            system, pool, vdisk, [template]);

        Assert.Equal(OperationIntent.DeleteStoragePool, proposal.Intent);
        Assert.Equal([pool, vdisk, template], proposal.Targets);
        Assert.Collection(proposal.Steps,
            step => Assert.IsType<DeleteVirtualDiskCommand>(step.Command),
            step => Assert.IsType<DeleteTierCommand>(step.Command),
            step => Assert.IsType<DeletePoolCommand>(step.Command));
        Assert.Equal(["delete-vdisk"], proposal.Steps[1].DependsOn);
        Assert.Equal(["delete-tier-1"], proposal.Steps[2].DependsOn);
        Assert.Contains("tier instances are absent", proposal.Steps[0].AfterCondition);
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
        Assert.Contains("createdByStep=", createText);

        var msr = new StorageObjectId(system, StorageObjectKind.Partition, "provider-msr-id");
        var layout = RealOperationProposalFactory.ConfigureInitializedDisk(
            system, disk, msr, true, 16L * 1024 * 1024 * 1024,
            true, "Data", 'E')!;
        Assert.Equal([disk, msr], layout.Targets);
        Assert.IsType<DeletePartitionCommand>(layout.Steps[0].Command);
        var createMsr = Assert.IsType<CreatePartitionCommand>(layout.Steps[1].Command);
        Assert.Equal(RealPartitionRole.Msr, createMsr.Role);
        Assert.Equal(1024L * 1024, createMsr.OffsetBytes);
        Assert.Equal(16L * 1024 * 1024, createMsr.SizeBytes);
        var data = Assert.IsType<CreatePartitionCommand>(layout.Steps[2].Command);
        Assert.Equal(RealPartitionRole.BasicData, data.Role);
        Assert.Equal(17L * 1024 * 1024, data.OffsetBytes);
        var format = Assert.IsType<FormatVolumeCommand>(layout.Steps[3].Command);
        Assert.Equal(RealFileSystem.Ntfs, format.FileSystem);
        Assert.Equal(["create-data"], layout.Steps[3].DependsOn);
        var letter = Assert.IsType<SetDriveLetterCommand>(layout.Steps[4].Command);
        Assert.Equal('E', letter.NewLetter);
        Assert.Contains("create-data", layout.Steps[4].DependsOn);
        Assert.Contains("format-data", layout.Steps[4].DependsOn);
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
        Assert.True(RealPartitionResizeUiRange.IsSupportedFileSystem("ReFS"));
        Assert.False(RealPartitionResizeUiRange.IsSupportedFileSystem("exFAT"));
        var refsRange = range with { AllowedMinBytes = range.CurrentSizeBytes };
        Assert.False(RealPartitionResizeUiRange.TryGetWholeMibTargets(
            refsRange, false, out _, out _));
        Assert.True(RealPartitionResizeUiRange.TryGetWholeMibTargets(
            refsRange, true, out _, out _));
    }

    [Fact]
    public void RebuildDeletionStageListsOnlyExactRemovalAndKeepsOnePhysicalMember()
    {
        var system = SystemId.New();
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "member-id");
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "old-pool-id");
        var disk = new StorageObjectId(system, StorageObjectKind.VirtualDisk, "old-vdisk-id");
        var tier = new StorageObjectId(system, StorageObjectKind.StorageTier, "old-template-id");
        var proposal = RealOperationProposalFactory.RebuildSingleMemberPool(
            system, physical, pool, disk, "NewPool",
            new RealOperationProposalFactory.VirtualDiskOptions(
                "NewDisk", 16L * 1024 * 1024 * 1024,
                true, true, true, "Data", 'E'), [tier]);

        Assert.Equal(OperationIntent.RebuildStoragePool, proposal.Intent);
        Assert.Equal([physical, pool, disk, tier], proposal.Targets);
        Assert.Collection(proposal.Steps,
            step => Assert.IsType<DeleteVirtualDiskCommand>(step.Command),
            step => Assert.IsType<DeleteTierCommand>(step.Command),
            step => Assert.IsType<DeletePoolCommand>(step.Command));
        Assert.Equal(["delete-vdisk"], proposal.Steps[1].DependsOn);
        Assert.Equal(["delete-tier-1"], proposal.Steps[2].DependsOn);
        Assert.DoesNotContain(proposal.Steps, step => step.Command is
            CreatePoolCommand or CreateVirtualDiskCommand or InitializeGptCommand or ClearDiskCommand);
        Assert.Contains("separate frozen confirmations", proposal.ExpectedFinalState);
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
