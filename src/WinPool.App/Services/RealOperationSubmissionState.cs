using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Retains one real operation across editor, mode and connection changes.</summary>
public sealed class RealOperationSubmissionState
{
    public bool IsBlocked { get; private set; }
    public long ReservationId { get; private set; }
    public OperationId? OperationId { get; private set; }
    public string? PlanHash { get; private set; }
    public event Action? Changed;

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
            || response.Plan.OperationId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(response.Plan.PlanHash))
            throw new InvalidOperationException("A reserved submission needs one exact prepared plan.");
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
            if (response.Plan.OperationId != operationId
                || !StringComparer.Ordinal.Equals(response.Plan.PlanHash, PlanHash))
                return false;
            if (terminal && !response.RequiresReconciliation)
            {
                OperationId = null;
                PlanHash = null;
                IsBlocked = false;
                Changed?.Invoke();
            }
            return true;
        }

        // A manual query after reconnect may discover an unfinished operation.
        if ((!terminal || response.RequiresReconciliation)
            && response.Plan.OperationId.Value != Guid.Empty
            && !string.IsNullOrWhiteSpace(response.Plan.PlanHash))
        {
            OperationId = response.Plan.OperationId;
            PlanHash = response.Plan.PlanHash;
            IsBlocked = true;
            Changed?.Invoke();
        }
        return true;
    }
}
