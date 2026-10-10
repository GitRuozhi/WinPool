using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

public sealed record RealOperationFeedbackMessage(string Title, string Message, GlobalNotificationOptions Options);

public static class RealOperationFeedback
{
    public static RealOperationFeedbackMessage ModeFailure(string code, bool entering, bool chinese,
        AgentRealOperationResponse? status, WinPoolFacts? facts = null)
    {
        var barrier = code == "agent.real_operation.write_barrier";
        var title = barrier
            ? (chinese ? "未核实的操作阻止开启真实编辑" : "An unresolved operation blocks real editing")
            : (chinese ? (entering ? "无法开启真实编辑" : "无法关闭真实编辑")
                : (entering ? "Cannot enable real editing" : "Cannot disable real editing"));
        var message = code switch
        {
            "agent.real_operation.write_barrier" => chinese
                ? "存在结果尚未核实的真实操作，暂时不能开启真实编辑。请到存储结构编辑或磁盘分区编辑页点击“查看操作结果”。"
                : "A real operation has an unresolved outcome. Open Storage structure or Disk partitions and select View operation result before enabling real editing.",
            "agent.real_operation.busy" => chinese ? "Agent 正在处理真实操作，请等待当前操作结束后重试。" : "The Agent is processing a real operation. Retry after it finishes.",
            "agent.real_mode.administrator_required" => chinese ? "App 和 Agent 需要管理员权限，请通过真实编辑开关重新启动为管理员。" : "The App and Agent require administrator privileges. Use the real editing switch to restart as administrator.",
            "agent.real_mode.another_session_armed" => chinese ? "另一个 WinPool 窗口已开启真实编辑，请先在该窗口关闭真实编辑。" : "Another WinPool window has real editing enabled. Disable it there first.",
            _ => chinese ? "Agent 未确认此次模式切换。具体错误和相关操作见开发页消息详情。" : "The Agent did not confirm the mode change. See the developer message details for the error and related operation."
        };
        if (barrier && status is not null) message += Environment.NewLine + ReconciliationReason(status, chinese);
        return new(title, message, Options(code, status, chinese, facts));
    }

    public static RealOperationFeedbackMessage Recovery(AgentRealOperationResponse status, bool chinese,
        WinPoolFacts? facts = null) => new(
        status.RequiresReconciliation
            ? (chinese ? (HasUnknownFormat(status) ? "格式化结果尚未核实" : "真实操作结果尚未核实") : "Real operation outcome remains unresolved")
            : (chinese ? "真实操作核对状态已更新" : "Real operation review updated"),
        ReconciliationReason(status, chinese) + Environment.NewLine
            + (chinese ? "请在原编辑页点击“查看操作结果”核对目标与各步结果。" : "Select View operation result in the editor to review the target and each step."),
        Options(status.ReconciliationDiagnostic?.Code ?? status.Code ?? "agent.real_operation.outcome_unknown", status, chinese, facts));

    public static string ReconciliationReason(AgentRealOperationResponse status, bool chinese)
    {
        var code = status.ReconciliationDiagnostic?.Code;
        if (HasUnknownFormat(status) && (code is null or "real.reconciliation_step_outcome_unknown"))
            return chinese ? "格式化未返回可核实的完成结果；当前只读核对无法确认其结果，后续步骤已停止，写入保护仍保留。"
                : "Formatting returned no verifiable completion result. Read-only review cannot establish its outcome; later steps remain stopped and write protection remains active.";
        return code switch
        {
            "real.reconciliation_step_outcome_unknown" => chinese ? "已有 Windows 调用的结果仍无法核实，写入保护仍保留。" : "An issued Windows call still has an unverifiable outcome; write protection remains active.",
            "real.reconciliation_capture_failed" or "operation.reconciliation_failed" => chinese ? "只读核对未能取得或验证所需事实；详细原因见开发页消息详情。" : "Read-only review could not obtain or validate the required facts. See the developer message details.",
            "real.reconciliation_physical_changed" or "real.reconciliation_machine_changed" or "real.reconciliation_topology_changed" => chinese ? "当前机器、目标磁盘或相关结构与冻结计划不一致，无法确认旧操作结果。" : "The current machine, target disk or related topology differs from the frozen plan; the old outcome cannot be confirmed.",
            _ => status.RequiresReconciliation
                ? (chinese ? "真实操作结果尚未核实，写入保护仍保留；核对代码和原步骤证据见开发页消息详情。" : "The outcome is unresolved and write protection remains active. Review codes and original step evidence are in the developer message details.")
                : (chinese ? "请根据各步核对结果决定后续操作；旧调用不会自动重放。" : "Review each step before deciding what to do next. Old calls are not replayed automatically.")
        };
    }

    public static string RecoveryKey(AgentRealOperationResponse status) =>
        $"{status.State}|{status.RequiresReconciliation}|{status.Code}|{status.ReconciliationDiagnostic?.Code}|{status.ReconciliationDiagnostic?.Detail}|"
        + string.Join('|', status.Steps.Select(step => $"{step.StepId}:{step.State}:{step.Code}"));

    private static bool HasUnknownFormat(AgentRealOperationResponse status) =>
        status.Plan.RealOperation?.Steps.Any(step => step.Command is FormatVolumeCommand
            && status.Steps.Any(progress => progress.StepId == step.Id && progress.State == RealOperationStepState.OutcomeUnknown)) == true;

    private static GlobalNotificationOptions Options(string code, AgentRealOperationResponse? status, bool chinese, WinPoolFacts? facts)
    {
        if (status is null) return new() { Code = code, Detail = code };
        var target = status.Plan.Targets.FirstOrDefault(item => item.Kind == StorageObjectKind.PhysicalDisk);
        if (target == default) target = status.Plan.Targets.FirstOrDefault();
        var detail = new List<string> { $"Code: {code}", $"OperationId: {status.Plan.OperationId.Value}" };
        if (status.ReconciliationDiagnostic is { } diagnostic)
        {
            detail.Add($"Reconciliation: {diagnostic.Code}");
            if (!string.IsNullOrWhiteSpace(diagnostic.Detail)) detail.Add(diagnostic.Detail[..Math.Min(768, diagnostic.Detail.Length)]);
        }
        foreach (var step in status.Steps.Where(step => step.State == RealOperationStepState.OutcomeUnknown))
        {
            detail.Add($"Step: {step.StepId}; Code: {step.Code}");
            if (!string.IsNullOrWhiteSpace(step.ResultEvidence)) detail.Add(step.ResultEvidence[..Math.Min(1024, step.ResultEvidence.Length)]);
        }
        detail.Add(RealOperationConfirmationFormatter.FormatStatus(status, chinese, facts));
        return new()
        {
            Code = code, SystemId = status.Plan.SystemId.Value.ToString("D"),
            Target = status.Plan.Targets.Count == 0 ? string.Empty : RealOperationConfirmationFormatter.FormatTarget(target, chinese, facts),
            Detail = string.Join(Environment.NewLine, detail)
        };
    }
}
