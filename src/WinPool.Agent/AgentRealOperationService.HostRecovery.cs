using System.Text.Json;
using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

public sealed partial class AgentRealOperationService
{
    private string? UnfinishedHostCallReason(string? targetJson, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(targetJson)) return null;
        PowerShellHostCall? call;
        try
        {
            using var target = JsonDocument.Parse(targetJson);
            // Pre-host records retain the existing backend's conservative recovery rules.
            if (target.RootElement.ValueKind != JsonValueKind.Object
                || !target.RootElement.TryGetProperty("HostCall", out var property)) return null;
            call = property.Deserialize<PowerShellHostCall>();
            if (call is null || call.Incarnation == Guid.Empty || call.RequestId == Guid.Empty
                || call.ProcessId <= 0 || call.StartedAtUtc == default)
                return "operation.host_call_evidence_invalid";
        }
        catch (JsonException)
        {
            // An unreadable target cannot establish the issued process identity.
            return "operation.host_call_evidence_invalid";
        }

        if (HasCompletedHostRequest(resultJson, call)) return null;
        var observation = processVerifier.Observe(call.ProcessId);
        if (observation.State == ProcessIncarnationState.Exited) return null;
        if (observation.State != ProcessIncarnationState.Alive || observation.Incarnation is not { } witness
            || witness.ProcessId != call.ProcessId || string.IsNullOrWhiteSpace(witness.ImagePath)
            || !Path.IsPathFullyQualified(witness.ImagePath) || witness.StartedAtUtc == default)
            return "operation.host_exit_unverifiable";
        // A different executable/creation time proves this PID is now another
        // process. Alive only blocks the exact old Windows PowerShell process.
        return ProcessIncarnationMatcher.Matches(witness, call, WindowsPowerShellRunner.ExecutablePath)
            ? "operation.host_request_may_be_running" : null;
    }

    private static bool HasCompletedHostRequest(string? resultJson, PowerShellHostCall call)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var result = JsonDocument.Parse(resultJson);
            var root = result.RootElement;
            if (IsCompletion(root, call)) return true;
            // Ordinary exact-member postconditions and both MAX receipts wrap
            // the adapter result in Provider; no-effect evidence uses ProviderResult.
            return root.ValueKind == JsonValueKind.Object
                && ((root.TryGetProperty("Provider", out var provider) && IsCompletion(provider, call))
                    || (root.TryGetProperty("ProviderResult", out provider) && IsCompletion(provider, call)));
        }
        catch (JsonException) { return false; }
    }

    private static bool IsCompletion(JsonElement provider, PowerShellHostCall call) =>
        provider.ValueKind == JsonValueKind.Object
        && provider.TryGetProperty("CompletedHostCall", out var completed)
        && completed.Deserialize<PowerShellHostCall>() == call;
}
