using WinPool.Application;

namespace WinPool.App.Services;

/// <summary>App-visible real mode is armed only by the Agent's reply.</summary>
public sealed class AgentRealModeSession(IAgentConnection connection)
{
    public string ProductSessionId { get; } = Guid.NewGuid().ToString("N");
    public bool IsArmed { get; private set; }

    public void DisarmLocally() => IsArmed = false;

    public async Task<string?> EnterAsync(CancellationToken cancellationToken)
    {
        IsArmed = false;
        var result = await connection.SendAsync(
            new EnterAgentRealModeRequest(ProductSessionId, CorrelationId.New()),
            cancellationToken);
        if (!result.IsSuccess || result.Value is not AgentRealModeResponse { IsArmed: true })
            return result.Messages.FirstOrDefault()?.Code
                ?? (result.Value as AgentRealModeResponse)?.Code
                ?? "agent.real_mode.rejected";
        IsArmed = true;
        return null;
    }

    public async Task<string?> ExitAsync(CancellationToken cancellationToken)
    {
        IsArmed = false;
        var result = await connection.SendAsync(
            new ExitAgentRealModeRequest(ProductSessionId, CorrelationId.New()),
            cancellationToken);
        return result.IsSuccess && result.Value is AgentRealModeResponse { IsArmed: false }
            ? null
            : result.Messages.FirstOrDefault()?.Code
                ?? (result.Value as AgentRealModeResponse)?.Code
                ?? "agent.real_mode.exit_uncertain";
    }
}
