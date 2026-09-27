using WinPool.Application;

namespace WinPool.App.Services;

public static class AgentShutdownReceiptPolicy
{
    public static bool AllowsDataLocationSwitch(
        ApplicationResult<AgentResponse> result) =>
        result.IsSuccess &&
        result.Value is ShutdownResponse { Result.Completed: true };
}
