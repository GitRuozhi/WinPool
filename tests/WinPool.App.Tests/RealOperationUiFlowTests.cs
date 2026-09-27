using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealOperationUiFlowTests
{
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
        return (proposal, plan);
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
