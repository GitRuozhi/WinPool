using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealOperationUiFlowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealModeBarrierFeedbackShowsReasonAndRetainsDeveloperEvidence(bool chinese)
    {
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition, "exact-format-partition");
        var plan = Freeze(RealOperationProposalFactory.OneStep(system, OperationIntent.FormatVolume, partition,
            new FormatVolumeCommand(RealTargetReference.ForExisting(partition), RealFileSystem.Ntfs, 65536, true, null),
            "NTFS", "All existing data is erased"));
        var step = Assert.Single(plan.RealOperation!.Steps);
        const string originalEvidence = "{\"Code\":\"adapter.response-outcome-unknown\",\"ProviderError\":\"The write process returned no structured result.\"}";
        var status = new AgentRealOperationResponse(plan, RealOperationState.OutcomeUnknown,
            [new(step.Id, RealOperationStepState.OutcomeUnknown, "real.provider_reported_uncertain_result", null, originalEvidence)],
            "operation.interrupted_requires_reconciliation", true,
            new("real.reconciliation_step_outcome_unknown"));
        var feedback = RealOperationFeedback.ModeFailure("agent.real_operation.write_barrier", true, chinese, status);
        Assert.Contains(chinese ? "阻止" : "blocks", feedback.Title);
        Assert.Contains(chinese ? "开发页消息详情" : "developer page", feedback.Message);
        Assert.DoesNotContain(chinese ? "查看操作结果" : "View operation result", feedback.Message);
        Assert.DoesNotContain(chinese ? "停止后续步骤" : "Stop following steps", feedback.Message);
        Assert.DoesNotContain("OperationId", feedback.Message);
        Assert.Contains(chinese ? "格式化未返回" : "Formatting returned no", feedback.Message);
        var notifications = new GlobalNotificationService();
        notifications.PublishError(feedback.Title, feedback.Message, "real-mode", options: feedback.Options);
        var retained = Assert.Single(notifications.History);
        Assert.Equal("agent.real_operation.write_barrier", retained.Code);
        Assert.Equal(system.Value.ToString("D"), retained.SystemId);
        Assert.Contains(partition.ProviderKey, retained.Target);
        Assert.Contains(plan.OperationId.Value.ToString(), retained.Detail);
        Assert.Contains("real.reconciliation_step_outcome_unknown", retained.Detail);
        Assert.Contains(originalEvidence, retained.Detail);
        Assert.Contains(chinese ? "无法确认其结果" : "cannot establish its outcome",
            RealOperationConfirmationFormatter.FormatStatus(status, chinese));
        Assert.Equal(RealOperationState.OutcomeUnknown, status.State);
        Assert.True(status.RequiresReconciliation);
        Assert.Equal(originalEvidence, status.Steps[0].ResultEvidence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealModeUnknownFailureAndReadOnlyProbeFailureKeepTechnicalDetails(bool chinese)
    {
        var unknown = RealOperationFeedback.ModeFailure("new.agent.failure", false, chinese, null);
        Assert.Equal(chinese ? "无法关闭真实编辑" : "Cannot disable real editing", unknown.Title);
        Assert.Equal("new.agent.failure", unknown.Options.Code);
        Assert.Contains("new.agent.failure", unknown.Options.Detail);
        Assert.Contains(chinese ? "开发页消息详情" : "developer message details", unknown.Message);
        Assert.DoesNotContain("new.agent.failure", unknown.Message);
        var (_, plan) = CreatePlan();
        var status = new AgentRealOperationResponse(plan, RealOperationState.OutcomeUnknown, [], null, true,
            new("operation.reconciliation_failed", "IOException: exact read-only probe failure"));
        var feedback = RealOperationFeedback.Recovery(status, chinese);
        Assert.Contains(chinese ? "只读核对未能" : "Read-only review could not", feedback.Message);
        Assert.Contains(chinese ? "开发页消息详情" : "developer message details", feedback.Message);
        Assert.DoesNotContain(chinese ? "查看操作结果" : "View operation result", feedback.Message);
        Assert.DoesNotContain(chinese ? "停止后续步骤" : "Stop following steps", feedback.Message);
        Assert.DoesNotContain("OperationId", feedback.Message);
        Assert.Equal("operation.reconciliation_failed", feedback.Options.Code);
        Assert.Contains("IOException: exact read-only probe failure", feedback.Options.Detail);
        Assert.Contains(plan.OperationId.Value.ToString(), feedback.Options.Detail);
    }

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

    [Theory]
    [InlineData(RealOperationState.Cancelled)]
    [InlineData(RealOperationState.Rejected)]
    [InlineData(RealOperationState.Failed)]
    [InlineData(RealOperationState.PartiallyCompleted)]
    [InlineData(RealOperationState.Succeeded)]
    public async Task RecoveryQueriesRetainedTerminalOperationMissingFromUnfinishedList(RealOperationState terminal)
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.OutcomeUnknown, [], null, true));
        var closed = new AgentRealOperationResponse(plan, terminal, [], "real.known_terminal", false);
        var connection = new RecordingConnection(request => ApplicationResult<AgentResponse>.Succeeded(
            request is QueryAgentRealOperationRequest query
                ? query.OperationId == plan.OperationId ? closed : throw new InvalidOperationException()
                : new AgentRecoverableRealOperationsResponse([]), request.CorrelationId));
        var observed = new List<AgentRealOperationResponse>();
        await RealOperationRecovery.RefreshAsync(connection, state, status =>
        {
            observed.Add(status);
            state.Observe(status);
        }, CancellationToken.None);

        Assert.False(state.IsBlocked);
        Assert.Same(closed, Assert.Single(observed));
        Assert.Collection(connection.Requests,
            request => Assert.Equal(plan.OperationId, Assert.IsType<QueryAgentRealOperationRequest>(request).OperationId),
            request => Assert.IsType<ListAgentRecoverableRealOperationsRequest>(request));
    }

    [Fact]
    public async Task ReadOnlyRecoveryDoesNotReconcileUntilExplicitRecheck()
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        var closed = false;
        AgentRealOperationResponse Full() => new(plan,
            closed ? RealOperationState.Failed : RealOperationState.OutcomeUnknown,
            [], closed ? "real.reconciliation_observed_no_effect" : null, !closed);
        AgentRealOperationStatusResponse Status() => new(plan.OperationId, closed ? 2 : 1,
            Full().State, [], Full().Code, !closed);
        var connection = new RecordingConnection(request =>
        {
            AgentResponse response;
            if (request is ReconcileAgentRealOperationRequest)
            {
                closed = true;
                response = Status();
            }
            else response = request switch
            {
                ListAgentRecoverableRealOperationsRequest => new AgentRecoverableRealOperationsResponse([Status()]),
                QueryAgentRealOperationRequest => Status(),
                LoadAgentRealOperationEvidenceRequest => new AgentRealOperationEvidenceResponse(Full(), 0, null),
                _ => throw new InvalidOperationException("Unexpected recovery request.")
            };
            return ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId);
        });
        await RealOperationRecovery.RefreshAsync(connection, state, status => state.Observe(status), CancellationToken.None);
        await RealOperationRecovery.RefreshAsync(connection, state, status => state.Observe(status), CancellationToken.None);
        Assert.True(state.IsBlocked);
        Assert.False(closed);
        Assert.DoesNotContain(connection.Requests, request => request is ReconcileAgentRealOperationRequest);

        await RealOperationRecovery.RecheckAsync(connection, state, status => state.Observe(status), CancellationToken.None);
        Assert.False(state.IsBlocked);
        Assert.Single(connection.Requests.OfType<ReconcileAgentRealOperationRequest>());
        Assert.Equal(2, connection.Requests.OfType<LoadAgentRealOperationEvidenceRequest>().Count());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryNeverClearsUnknownOrMismatchedTerminalResponse(bool samePlanHash)
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.OutcomeUnknown, [], null, true));
        var queried = new AgentRealOperationResponse(samePlanHash ? plan : plan with { PlanHash = "another-plan" },
            samePlanHash ? RealOperationState.OutcomeUnknown : RealOperationState.Succeeded,
            [], null, samePlanHash);
        var connection = new RecordingConnection(request => ApplicationResult<AgentResponse>.Succeeded(
            request is QueryAgentRealOperationRequest ? queried : new AgentRecoverableRealOperationsResponse([]),
            request.CorrelationId));
        await RealOperationRecovery.RefreshAsync(connection, state, status => state.Observe(status), CancellationToken.None);
        Assert.True(state.IsBlocked);
        Assert.Equal(plan.OperationId, state.OperationId);
        Assert.Equal(plan.PlanHash, state.PlanHash);
    }

    [Fact]
    public async Task RecoveryRetainsExactTerminalWhileRelatedInventoryRefreshNeedsRetry()
    {
        var (_, plan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.OutcomeUnknown, [], null, true));
        var closed = new AgentRealOperationResponse(plan, RealOperationState.Succeeded, [], null, false);
        var connection = new RecordingConnection(request => ApplicationResult<AgentResponse>.Succeeded(
            request is QueryAgentRealOperationRequest ? closed : new AgentRecoverableRealOperationsResponse([]),
            request.CorrelationId));

        AgentRealOperationResponse? awaitingRefresh = null;
        await RealOperationRecovery.RefreshAsync(connection, state,
            status => awaitingRefresh = status, CancellationToken.None);
        Assert.Same(closed, awaitingRefresh);
        Assert.True(state.IsBlocked);
        Assert.Equal(plan.OperationId, state.OperationId);

        // The next read-only refresh still queries the retained terminal ID.
        // Its consumer applies the terminal only after fresh related facts.
        await RealOperationRecovery.RefreshAsync(connection, state,
            status => state.Observe(status), CancellationToken.None);
        Assert.False(state.IsBlocked);
        Assert.Equal(2, connection.Requests.OfType<QueryAgentRealOperationRequest>().Count());
        Assert.All(connection.Requests, request => Assert.True(request is
            QueryAgentRealOperationRequest or ListAgentRecoverableRealOperationsRequest));
    }

    [Fact]
    public async Task RecoverySurfacesAnotherUnfinishedIdentityBeforeTerminalInventoryRefreshFinishes()
    {
        var (_, plan) = CreatePlan();
        var (_, otherPlan) = CreatePlan();
        var state = new RealOperationSubmissionState();
        state.Observe(new AgentRealOperationResponse(plan, RealOperationState.OutcomeUnknown, [], null, true));
        var closed = new AgentRealOperationResponse(plan, RealOperationState.Succeeded, [], null, false);
        var unfinished = new AgentRealOperationResponse(otherPlan, RealOperationState.OutcomeUnknown, [], null, true);
        var connection = new RecordingConnection(request => ApplicationResult<AgentResponse>.Succeeded(
            request switch
            {
                QueryAgentRealOperationRequest => closed,
                ListAgentRecoverableRealOperationsRequest => new AgentRecoverableRealOperationsResponse([
                    new(otherPlan.OperationId, 1, unfinished.State, unfinished.Steps, unfinished.Code, true)]),
                LoadAgentRealOperationEvidenceRequest => new AgentRealOperationEvidenceResponse(unfinished, 0, null),
                _ => throw new InvalidOperationException()
            },
            request.CorrelationId));
        var observations = new List<AgentRealOperationResponse>();
        await RealOperationRecovery.RefreshAsync(connection, state, observations.Add, CancellationToken.None);

        Assert.Equal([plan.OperationId, otherPlan.OperationId], observations.Select(status => status.Plan.OperationId));
        Assert.True(state.IsBlocked);
        // Once the first operation's scoped refresh completes, the other
        // listed identity remains available to retain the global barrier.
        Assert.True(state.Observe(observations[0]));
        Assert.True(state.Observe(observations[1]));
        Assert.True(state.IsBlocked);
        Assert.Equal(otherPlan.OperationId, state.OperationId);
    }

    [Fact]
    public void DataLocationReplacementWaitsForOldAppAndReopensSettingsWithoutRealMode()
    {
        var executable = Path.Combine(Path.GetTempPath(), "WinPool", "WinPool.App.exe");
        var start = DataLocationSwitchRuntime.CreateReplacementStartInfo(executable, 42);
        var arguments = start.ArgumentList.ToArray();

        Assert.Equal(executable, start.FileName);
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
        Assert.True(ApplicationStartupOptions.RequestsProcessHandoff(arguments));
        Assert.Equal(42, ApplicationStartupOptions.GetHandoffProcessId(arguments));
        var options = ApplicationStartupOptions.Parse(arguments, PrivilegeState.Administrator);
        Assert.Equal(ApplicationStartupTarget.Settings, options.Target);
        Assert.False(options.EnterRealModeAfterElevation);
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
        var proposal = RealOperationProposalFactory.CreatePoolWithMembers(
            system, [physical], "NewPool");

        Assert.Equal(OperationIntent.CreateStoragePool, proposal.Intent);
        Assert.Equal(physical, Assert.Single(proposal.Targets));
        var step = Assert.Single(proposal.Steps);
        Assert.Equal("create-pool", step.Id);
        Assert.Empty(step.DependsOn);
        var command = Assert.IsType<CreatePoolCommand>(step.Command);
        Assert.Equal(physical, command.PhysicalDisk.Existing);
        Assert.Equal([RealTargetReference.ForExisting(physical)], command.EffectiveMembers);
        Assert.Equal("NewPool", command.Name);
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
        var refsCreate = RealOperationProposalFactory.CreatePartition(
            system, disk, RealPartitionRole.BasicData, 1024 * 1024,
            1024L * 1024 * 1024, RealFileSystem.ReFs, 65536,
            false, "Data", null);
        Assert.Equal(RealFileSystem.ReFs,
            Assert.IsType<FormatVolumeCommand>(refsCreate.Steps.Single(step => step.Command is FormatVolumeCommand).Command).FileSystem);
    }

    [Fact]
    public void ExistingRealFormatAllowsSupportedRefsClustersQuickOnlyAndRejectsUnknown()
    {
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition,
            "partition-id");
        Assert.NotNull(RealOperationProposalFactory.TryFormatExistingData(
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
        Assert.NotNull(RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "NTFS", 4096, false, "Data"));
        Assert.NotNull(RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "exFAT", 33_554_432, false, "Data"));
        var refs = RealOperationProposalFactory.TryFormatExistingData(
            system, partition, "ReFS", 65536, false, "Data");
        Assert.Equal(RealFileSystem.ReFs,
            Assert.IsType<FormatVolumeCommand>(Assert.Single(refs!.Steps).Command).FileSystem);
    }

    [Theory]
    [InlineData("NTFS", 4096)]
    [InlineData("ReFS", 4096)]
    public void AutomaticInitializationCarriesSelectedFilesystemAndCluster(string fileSystem, int clusterBytes)
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk-id");
        var layout = RealOperationProposalFactory.ConfigureInitializedDisk(
            system, disk, null, false, 16L * 1024 * 1024 * 1024,
            formatNtfs: true, label: "Data", letter: 'E',
            allocationUnitBytes: clusterBytes, formatFileSystem: fileSystem)!;
        var format = Assert.IsType<FormatVolumeCommand>(layout.Steps.Single(step => step.Command is FormatVolumeCommand).Command);
        Assert.Equal(Enum.Parse<RealFileSystem>(fileSystem, ignoreCase: true), format.FileSystem);
        Assert.Equal(clusterBytes, format.ClusterBytes);
        Assert.False(format.Full);
    }

    [Fact]
    public void RealHddTierActionsUseExactPoolAndTierTargets()
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var tier = new StorageObjectId(system, StorageObjectKind.StorageTier, "tier-id");
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "member-id");
        var createTier = RealOperationProposalFactory.CreateTierTemplate(
            system, pool, [physical], "HDDTemplate", "HDD", StorageLayoutParameters.LegacyDefault);
        Assert.Equal(OperationIntent.CreateStorageTier, createTier.Intent);
        Assert.Equal([pool, physical], createTier.Targets);
        var createTierCommand = Assert.IsType<CreateTierCommand>(
            Assert.Single(createTier.Steps).Command);
        Assert.Equal(pool, createTierCommand.Pool.Existing);
        Assert.Equal("HDDTemplate", createTierCommand.Name);
        Assert.Equal(65536, createTierCommand.InterleaveBytes);
        Assert.Equal(1, createTierCommand.DataColumns);

        var createVdisk = RealOperationProposalFactory.CreateTieredVirtualDisk(
            system, pool, [physical], [new(RealTargetReference.ForExisting(tier), null,
                16L * 1024 * 1024 * 1024, false, StorageLayoutParameters.LegacyDefault, "HDD")], "HDDData");
        Assert.Equal(OperationIntent.CreateVirtualDisk, createVdisk.Intent);
        Assert.Equal([pool, physical, tier], createVdisk.Targets);
        var createVdiskCommand = Assert.IsType<CreateTieredVirtualDiskCommand>(
            Assert.Single(createVdisk.Steps).Command);
        Assert.Equal(pool, createVdiskCommand.Pool.Existing);
        Assert.Equal(tier, createVdiskCommand.Tier.Existing);
        Assert.Equal(16L * 1024 * 1024 * 1024, createVdiskCommand.SizeBytes);

        var delete = RealOperationProposalFactory.DeleteHddTierTemplate(system, tier, pool, [physical]);
        Assert.Equal(OperationIntent.DeleteStorageTier, delete.Intent);
        Assert.Equal([tier, pool, physical], delete.Targets);
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
    public void FrozenConfirmationShowsReadableTargetsChangesAndDataImpactWithoutInternalIds()
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
        Assert.Contains("清空为 RAW", text);
        Assert.Contains("数据影响", text);
        Assert.DoesNotContain("OperationId", text);
        Assert.DoesNotContain("Plan hash", text);
        Assert.DoesNotContain("WDC WD40EZAZ", text); // BeforeCondition prose is not used as identity.

        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool-id");
        var create = RealOperationProposalFactory.CreateFirstVirtualDisk(
            system, pool, new RealOperationProposalFactory.VirtualDiskOptions(
                "NewVD", 16L * 1024 * 1024 * 1024,
                true, true, true, "Data", 'E'));
        var createText = RealOperationConfirmationFormatter.Format(Freeze(create), false);
        Assert.Contains("pool-id", createText);
        Assert.Contains("17,179,869,184 bytes", createText);
        Assert.Contains("65,536 bytes interleave", createText);
        Assert.Contains("virtual disk “NewVD”", createText);
        Assert.DoesNotContain("createdByStep", createText);
        Assert.DoesNotContain("operation-1", createText);

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

    [Theory]
    [InlineData(true, true, 1024, 32, 1056, 32)]
    [InlineData(true, false, 1024, 1056, 1056, 32)]
    [InlineData(false, true, 1024, 32, 992, 32)]
    [InlineData(false, false, 1024, 992, 992, 32)]
    public void ResizeFormulaKeepsDeltaAndTargetInputsLinked(
        bool extend,
        bool inputIsDelta,
        long currentMib,
        long inputMib,
        long expectedTargetMib,
        long expectedDeltaMib)
    {
        const long mib = 1024L * 1024;

        Assert.True(RealPartitionResizeUiRange.TryGetResizeFormula(
            currentMib * mib,
            inputMib,
            inputIsDelta,
            extend,
            out var targetBytes,
            out var deltaBytes));

        Assert.Equal(expectedTargetMib * mib, targetBytes);
        Assert.Equal(expectedDeltaMib * mib, deltaBytes);
    }

    [Fact]
    public void ResizeFormulaKeepsFractionalCurrentBytesAndRejectsOverflowZeroAndWrongDirection()
    {
        const long mib = 1024L * 1024;

        const long exactCurrentBytes = 100 * mib + 1;
        Assert.True(RealPartitionResizeUiRange.TryGetResizeFormula(
            exactCurrentBytes, 1, true, true, out var targetBytes, out var deltaBytes));
        Assert.Equal(101 * mib, targetBytes);
        Assert.Equal(mib - 1, deltaBytes);
        Assert.Equal(exactCurrentBytes, targetBytes - deltaBytes);
        Assert.True(RealPartitionResizeUiRange.TryGetResizeFormula(
            exactCurrentBytes, 99, false, false, out targetBytes, out deltaBytes));
        Assert.Equal(99 * mib, targetBytes);
        Assert.Equal(mib + 1, deltaBytes);
        Assert.Equal(exactCurrentBytes, targetBytes + deltaBytes);
        Assert.False(RealPartitionResizeUiRange.TryGetResizeFormula(
            100 * mib, 0, true, true, out _, out _));
        Assert.False(RealPartitionResizeUiRange.TryGetResizeFormula(
            100 * mib, 100, true, false, out _, out _));
        Assert.False(RealPartitionResizeUiRange.TryGetResizeFormula(
            100 * mib, 100, false, true, out _, out _));
        Assert.False(RealPartitionResizeUiRange.TryGetResizeFormula(
            100 * mib, 101, false, false, out _, out _));
        Assert.False(RealPartitionResizeUiRange.TryGetResizeFormula(
            long.MaxValue - mib + 1, long.MaxValue / mib, true, true, out _, out _));
        Assert.True(RealPartitionResizeUiRange.TryGetWholeMibTargets(
            exactCurrentBytes, 1, 200 * mib, true, out var minimum, out var maximum));
        Assert.Equal(101, minimum);
        Assert.Equal(200, maximum);
        Assert.True(RealPartitionResizeUiRange.TryGetWholeMibTargets(
            exactCurrentBytes, mib, 200 * mib, false, out minimum, out maximum));
        Assert.Equal(1, minimum);
        Assert.Equal(100, maximum);
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
