using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

public interface IApplicationCommand;

public interface IApplicationCommandHandler<in TCommand, TResult>
    where TCommand : IApplicationCommand
{
    Task<ApplicationResult<TResult>> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken);
}

public sealed record RefreshInventoryCommand(
    InventoryRequest Request,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record SaveWorkspaceCommand(
    WorkspaceState State,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record RequestOperationPlanCommand(
    OperationRequest Request,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record StartTestCommand(
    TestDefinition Definition,
    TestTarget Target,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record CancelTestCommand(
    TestRunId RunId,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record StartMonitoringCommand(
    MonitorRequest Request,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record StopMonitoringCommand(
    SessionId SessionId,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record DetectExternalToolCommand(
    ToolId ToolId,
    CorrelationId CorrelationId) : IApplicationCommand;

public sealed record PlanExternalToolInstallCommand(
    ToolId ToolId,
    ToolInstallLocation Location,
    CorrelationId CorrelationId) : IApplicationCommand;
