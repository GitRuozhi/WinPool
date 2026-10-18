using WinPool.Application;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Agent;

public sealed partial class AgentRealOperationService
{
    private async Task<ApplicationResult<AgentResponse>> LoadEvidencePageAsync(
        LoadAgentRealOperationEvidenceRequest request, CancellationToken cancellationToken)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 16)
            return Reject(request.CorrelationId, "agent.real_operation.invalid_evidence_window");
        var operation = await plans.GetAsync(request.OperationId, cancellationToken);
        if (operation is null) return Reject(request.CorrelationId, "agent.real_operation.not_found");
        var status = await plans.ReadRealStatusAsync(request.OperationId, cancellationToken);
        if (status is null) return Reject(request.CorrelationId, "agent.real_operation.not_found");
        RealEvidenceWindow? window = null;
        PersistedEvidencePage page;
        if (request.StepId is not null)
        {
            if (request.Field is not { } field || request.ExpectedRevision is not { } revision)
                return Reject(request.CorrelationId, "agent.real_operation.invalid_evidence_window");
            try
            {
                window = await plans.ReadEvidenceWindowAsync(request.OperationId, request.StepId, field,
                    request.ByteOffset, request.ByteCount, revision, cancellationToken);
            }
            catch (ArgumentException) { return Reject(request.CorrelationId, "agent.real_operation.invalid_evidence_window"); }
            if (window is null) return Reject(request.CorrelationId, "agent.real_operation.evidence_revision_changed");
            page = new([], [], new Dictionary<string, string>());
        }
        else page = await plans.ReadEvidencePageAsync(request.OperationId, request.Offset, request.Limit, cancellationToken);
        var steps = page.Steps.Select(step => new RealOperationStepProgress(step.StepId,
            ToPublicState(step.State), page.Codes.GetValueOrDefault(step.StepId), step.TargetJson, step.EvidenceJson,
            status.Steps.Where(value => value.StepId == step.StepId).Select(value =>
                new RealOperationResultIdentity(value.CreatedObjectId, value.CreatedOsDiskId)).SingleOrDefault())).ToArray();
        var response = new AgentRealOperationResponse(operation.Plan, ToPublicState(status.State), steps,
            status.Code, status.State == PersistedOperationState.OutcomeUnknown,
            reconciliationDiagnostics.GetValueOrDefault(request.OperationId));
        var next = request.Offset + steps.Length;
        return ApplicationResult<AgentResponse>.Succeeded(new AgentRealOperationEvidenceResponse(response,
            request.Offset, window is null && next < operation.Plan.Steps.Count ? next : null, page.References, window), request.CorrelationId);
    }
}
