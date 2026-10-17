using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Retains one real operation across editor, mode and connection changes.</summary>
public sealed class RealOperationSubmissionState
{
    private const int TerminalIdentityHistoryLimit = 128;
    private readonly Queue<OperationIdentity> _terminalIdentityOrder = new();
    private readonly HashSet<OperationIdentity> _terminalIdentities = [];

    public bool IsBlocked { get; private set; }
    public long ReservationId { get; private set; }
    public OperationId? OperationId { get; private set; }
    public string? PlanHash { get; private set; }
    public event Action? Changed;

    public static bool IsReconciledTerminal(AgentRealOperationResponse response) =>
        !response.RequiresReconciliation && response.State is RealOperationState.Succeeded
            or RealOperationState.Rejected or RealOperationState.Cancelled
            or RealOperationState.Failed or RealOperationState.PartiallyCompleted;

    public bool TryReservePreparation()
    {
        if (IsBlocked)
            return false;
        ReservationId++;
        IsBlocked = true;
        Changed?.Invoke();
        return true;
    }

    public void ReleasePreparation(long reservationId)
    {
        // Once an exact plan is known, only its authoritative terminal status
        // can release the barrier, including cancellation before acceptance.
        if (reservationId == ReservationId && OperationId is null && IsBlocked)
        {
            IsBlocked = false;
            Changed?.Invoke();
        }
    }

    public bool TrackPrepared(AgentRealOperationResponse response)
    {
        if (!IsBlocked || response.State != RealOperationState.Prepared
            || !TryGetIdentity(response, out var identity))
            throw new InvalidOperationException("A reserved submission needs one exact prepared plan.");
        if (_terminalIdentities.Contains(identity))
            return false;
        if (OperationId is { } operationId)
            return response.Plan.OperationId == operationId
                && StringComparer.Ordinal.Equals(response.Plan.PlanHash, PlanHash);
        OperationId = response.Plan.OperationId;
        PlanHash = response.Plan.PlanHash;
        Changed?.Invoke();
        return true;
    }

    public bool Observe(AgentRealOperationResponse response)
    {
        var terminal = response.State is RealOperationState.Succeeded or RealOperationState.Rejected
            or RealOperationState.Cancelled or RealOperationState.Failed or RealOperationState.PartiallyCompleted;
        if (OperationId is { } operationId)
        {
            if (!TryGetIdentity(response, out var identity)
                || identity.OperationId != operationId
                || !StringComparer.Ordinal.Equals(identity.PlanHash, PlanHash))
                return false;
            if (IsReconciledTerminal(response))
            {
                RememberTerminal(identity);
                OperationId = null;
                PlanHash = null;
                IsBlocked = false;
                Changed?.Invoke();
            }
            return true;
        }

        if (!TryGetIdentity(response, out var observedIdentity))
            return true;

        // A reconciled terminal status is a tombstone for this exact operation
        // identity. Ignore a delayed Running/Unknown response so it cannot
        // reattach after the barrier was released or claim a later reservation.
        if (_terminalIdentities.Contains(observedIdentity))
            return IsReconciledTerminal(response);

        if (IsReconciledTerminal(response))
        {
            RememberTerminal(observedIdentity);
            return true;
        }

        // A manual query after reconnect may discover an unfinished operation.
        if ((!terminal || response.RequiresReconciliation)
            && observedIdentity.OperationId.Value != Guid.Empty)
        {
            OperationId = observedIdentity.OperationId;
            PlanHash = observedIdentity.PlanHash;
            IsBlocked = true;
            Changed?.Invoke();
        }
        return true;
    }

    private static bool TryGetIdentity(AgentRealOperationResponse response, out OperationIdentity identity)
    {
        var operationId = response.Plan.OperationId;
        var planHash = response.Plan.PlanHash;
        if (operationId.Value == Guid.Empty || string.IsNullOrWhiteSpace(planHash))
        {
            identity = default;
            return false;
        }

        identity = new OperationIdentity(operationId, planHash);
        return true;
    }

    private void RememberTerminal(OperationIdentity identity)
    {
        if (!_terminalIdentities.Add(identity))
            return;

        _terminalIdentityOrder.Enqueue(identity);
        while (_terminalIdentityOrder.Count > TerminalIdentityHistoryLimit)
            _terminalIdentities.Remove(_terminalIdentityOrder.Dequeue());
    }

    private readonly record struct OperationIdentity(OperationId OperationId, string PlanHash);
}
