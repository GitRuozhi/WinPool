using WinPool.Application;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>A single UI action's exact scope; never a general real-mode grant.</summary>
public interface IRealOperationAuthorization
{
    void ValidateProposal(RealOperationIntentRequest proposal);
    void ValidateFrozen(RealOperationIntentRequest proposal, AgentRealOperationResponse frozen);
}
