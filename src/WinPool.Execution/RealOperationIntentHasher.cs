using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinPool.Execution;

public static class RealOperationIntentHasher
{
    // Stable across Prepare retries. OperationId is assigned only after this key is checked.
    public static string Compute(
        RealOperationIntentRequest proposal,
        TrustedRealSession session,
        string machineBinding)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineBinding);
        if (!session.IsWellFormed || proposal.Targets is null || proposal.Steps is null ||
            proposal.Steps.Any(step => step is null || step.Command is null || step.DependsOn is null))
        {
            throw new ArgumentException("A complete trusted session and typed proposal are required.");
        }

        var canonical = new
        {
            proposal.Intent,
            proposal.SystemId,
            Targets = proposal.Targets
                .Select(target => new { target.System, target.Kind, target.ProviderKey })
                .OrderBy(target => target.System.Value)
                .ThenBy(target => target.Kind)
                .ThenBy(target => target.ProviderKey, StringComparer.Ordinal),
            Steps = proposal.Steps.Select(step => new
            {
                step.Id,
                step.Command,
                DependsOn = step.DependsOn.Order(StringComparer.Ordinal),
                step.BeforeCondition,
                step.AfterCondition,
                step.DataLoss,
                step.SupportEvidence
            }),
            proposal.ExpectedFinalState,
            SessionBinding = session.Binding,
            MachineBinding = machineBinding
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
