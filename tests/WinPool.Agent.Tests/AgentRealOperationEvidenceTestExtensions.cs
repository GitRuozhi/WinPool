using WinPool.Agent;
using WinPool.Application;
using WinPool.Execution;

namespace WinPool.Agent.Tests;

internal static class AgentRealOperationEvidenceTestExtensions
{
    public static async Task<ApplicationResult<AgentResponse>> QueryWithEvidenceAsync(
        this AgentRealOperationService service, QueryAgentRealOperationRequest request,
        TrustedRealSession session, CancellationToken cancellationToken)
    {
        var status = await service.QueryAsync(request, session, cancellationToken);
        return status.IsSuccess ? await LoadAsync(service, request.OperationId, session, cancellationToken) : status;
    }

    public static async Task<ApplicationResult<AgentResponse>> AcceptWithEvidenceAsync(
        this AgentRealOperationService service, AcceptAgentRealOperationRequest request,
        TrustedRealSession session, CancellationToken cancellationToken)
    {
        var status = await service.AcceptAsync(request, session, cancellationToken);
        return status.IsSuccess ? await LoadAsync(service, request.OperationId, session, CancellationToken.None) : status;
    }

    private static async Task<ApplicationResult<AgentResponse>> LoadAsync(AgentRealOperationService service,
        WinPool.Domain.OperationId id, TrustedRealSession session, CancellationToken cancellationToken)
    {
        var evidence = await service.LoadEvidenceAsync(new(id, 0, 16, CorrelationId.New()), session, cancellationToken);
        return evidence.Value is AgentRealOperationEvidenceResponse response
            ? evidence with { Value = response.Operation } : evidence;
    }
}
