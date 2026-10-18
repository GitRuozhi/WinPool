using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Elapsed observation is presentation, never evidence that a provider succeeded or died.</summary>
public sealed class RealOperationWaitClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private (OperationId Operation, string Step)? identity;
    private RealOperationStepState phase;
    private long observedAt;
    private long elapsedAtObservation;
    public bool IsActive => identity is not null;

    public void Observe(OperationId operationId, RealOperationState state,
        IReadOnlyList<RealOperationStepProgress> steps, long elapsedMilliseconds)
    {
        var active = state is RealOperationState.Accepted or RealOperationState.Running
            ? steps.FirstOrDefault(step => step.State is RealOperationStepState.PreparingCall
                or RealOperationStepState.CallIssued or RealOperationStepState.WaitingForProvider or RealOperationStepState.Verifying)
            : null;
        if (active is null) { identity = null; return; }
        var key = (operationId, active.StepId);
        var currentElapsed = identity is null ? 0 : elapsedAtObservation + (long)clock.GetElapsedTime(observedAt).TotalMilliseconds;
        var newCall = identity != key || phase == RealOperationStepState.PreparingCall
            && active.State != RealOperationStepState.PreparingCall;
        if (newCall || phase != active.State || elapsedMilliseconds > currentElapsed)
        {
            identity = key;
            phase = active.State;
            elapsedAtObservation = newCall ? Math.Max(0, elapsedMilliseconds) : Math.Max(currentElapsed, elapsedMilliseconds);
            observedAt = clock.GetTimestamp();
        }
    }

    public string? Format(bool chinese)
    {
        if (identity is not { } current) return null;
        var elapsed = elapsedAtObservation + (long)clock.GetElapsedTime(observedAt).TotalMilliseconds;
        var seconds = Math.Max(0, elapsed / 1000);
        var message = phase switch
        {
            RealOperationStepState.PreparingCall => chinese ? "正在准备Windows调用" : "Preparing the Windows call",
            RealOperationStepState.Verifying => chinese ? "正在核对调用结果" : "Verifying the call result",
            _ when seconds >= 60 => chinese ? "等待系统返回，结果尚未确认" : "Waiting for Windows; the outcome is not confirmed",
            _ => chinese ? "等待系统返回" : "Waiting for Windows"
        };
        return chinese ? $"{message} · {current.Step} · 已等待 {seconds} 秒"
            : $"{message} · {current.Step} · {seconds} s elapsed";
    }
}
