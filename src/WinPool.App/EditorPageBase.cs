using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using SimulationEditRequest = WinPool.Application.SimulationEditRequest;
using SimulationOperationResult = WinPool.Application.SimulationEditReceipt;

namespace WinPool_App;

/// <summary>
/// Shared plumbing for the two V0.47 editor pages: the working simulation
/// snapshot, typed simulation-operation submission, small dialogs, and the
/// size parsing helpers used by both editors.
/// </summary>
public class EditorPageBase : Page
{
    protected WorkspaceViewModel ViewModel { get; set; } = null!;
    protected SimulationEditingSession EditingSession { get; } = new();
    protected StorageSnapshot _working { get => EditingSession.Working; set => EditingSession.Working = value; }

    protected const double MinTopologyWidth = 320;
    protected const double TopologyWidthMargin = 20;

    protected long UnallocatedIgnoreBytes =>
        Math.Max(0, ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);

    protected string Text(string zh, string en) =>
        ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? zh : en;

    protected static long ParseSize(string token) =>
        token.Replace("KiB", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('K', 'k') is var digits && long.TryParse(digits, out var value)
            ? value * 1024
            : 65536;

    protected static int? ParseInt(string text) =>
        int.TryParse(text, out var value) ? value : null;

    protected static long? ParseGigabytes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryParseGigabytes(text, out var gb))
        {
            return null;
        }

        return (long)(gb * 1024L * 1024L * 1024L);
    }

    protected static bool TryParseGigabytes(string text, out double gigabytes)
    {
        gigabytes = 0;
        if (!double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || !double.IsFinite(parsed)
            || parsed <= 0)
        {
            return false;
        }

        gigabytes = parsed;
        return true;
    }

    protected async Task<SimulationOperationResult?> ApplyAsync(SimulationEditRequest request)
    {
        WinPool.Application.ApplicationResult<SimulationOperationResult> result;
        try
        {
            result = await ViewModel.ApplySimulationOperationAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            PublishOperationException(
                Text("操作失败", "Operation failed"),
                "editor",
                exception,
                "editor.apply.exception");
            return null;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            PublishOperationResult(
                result.Status,
                result.Messages,
                result.CorrelationId,
                Text("操作未完成", "Operation did not complete"),
                "editor");
            return null;
        }

        return result.Value;
    }

    protected async Task<string?> PromptAsync(string title, string value)
    {
        var input = new TextBox { Text = value, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await DialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary
            ? input.Text.Trim()
            : null;
    }

    protected async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 500,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
            },
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await DialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    protected async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 500,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
            },
            CloseButtonText = Text("关闭", "Close")
        };
        await DialogCoordinator.ShowAsync(dialog);
    }

    protected async Task<bool> SubmitRealAsync(RealOperationIntentRequest intent)
    {
        if (!ViewModel.CanSubmitRealOperation || ViewModel.AgentConnection is null)
            return false;

        try
        {
            RealOperationValidator.Validate(intent);
        }
        catch (ArgumentException exception)
        {
            await ShowMessageAsync(Text("真实操作参数不受支持", "Real operation parameters are unsupported"),
                exception.Message);
            return false;
        }

        var connection = ViewModel.AgentConnection;
        var flow = new RealOperationRequestSession(connection, ViewModel.RealProductSessionId);
        ApplicationResult<AgentResponse>? prepared = null;
        try
        {
            prepared = await flow.PrepareAsync(intent, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishOperationException(Text("真实准备失败", "Real preparation failed"),
                "real", exception, "real.prepare.exception");
            return false;
        }

        if (prepared is null || !prepared.IsSuccess ||
            prepared.Value is not AgentRealOperationResponse { State: RealOperationState.Prepared } frozen ||
            frozen.Plan.RealOperation is null)
        {
            if (prepared is not null)
                PublishOperationResult(prepared.Status, prepared.Messages, prepared.CorrelationId,
                    Text("真实准备未完成", "Real preparation did not complete"), "real");
            return false;
        }

        var plan = frozen.Plan;
        var confirmation = RealOperationConfirmationFormatter.Format(plan,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn);
        if (!await ConfirmAsync(Text("确认真实磁盘写入", "Confirm real disk write"), confirmation))
            return false;
        flow.Confirm(frozen);

        ApplicationResult<AgentResponse>? accepted = null;
        try
        {
            accepted = await flow.AcceptOnceAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishOperationException(Text("提交结果待核对", "Submission needs reconciliation"),
                "real", exception, "real.accept.uncertain");
        }

        if (accepted is { IsSuccess: false } && accepted.Status != ApplicationStatus.OutcomeUnknown)
        {
            PublishOperationResult(accepted.Status, accepted.Messages, accepted.CorrelationId,
                Text("真实提交被拒绝", "Real submission was rejected"), "real");
            return false;
        }

        // Accept is short-lived. Query the assigned identity after a lost reply;
        // never issue a second write request to infer whether the first ran.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            ApplicationResult<AgentResponse> status;
            try
            {
                status = await flow.QueryAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                PublishOperationException(Text("真实状态查询失败", "Real status query failed"),
                    "real", exception, "real.query.failed");
                return false;
            }
            if (!status.IsSuccess || status.Value is not AgentRealOperationResponse current)
                continue;
            if (current.State is RealOperationState.Accepted or RealOperationState.Running)
                continue;
            var succeeded = current.State == RealOperationState.Succeeded;
            await ShowMessageAsync(
                succeeded ? Text("真实操作完成", "Real operation completed")
                    : Text("真实操作需要核对", "Real operation needs review"),
                $"OperationId: {plan.OperationId.Value}\n" +
                $"{Text("状态", "State")}: {current.State}\n" +
                $"{Text("代码", "Code")}: {current.Code ?? "-"}");
            if (succeeded)
                await ViewModel.ScanAsync();
            return succeeded;
        }

        await ShowMessageAsync(Text("真实操作继续执行", "Real operation continues"),
            $"OperationId: {plan.OperationId.Value}\n" +
            Text("请稍后按此 OperationId 查询持久化状态。",
                "Query this OperationId later for its persisted status."));
        return false;
    }

    protected async Task QueryRealOperationByIdAsync()
    {
        if (ViewModel.AgentConnection is null)
            return;
        var raw = await PromptAsync(Text("查询真实操作", "Query real operation"), string.Empty);
        if (raw is null)
            return;
        if (!Guid.TryParse(raw, out var parsed) || parsed == Guid.Empty)
        {
            await ShowMessageAsync(Text("操作 ID 无效", "Invalid operation ID"),
                Text("请输入确认时显示的完整 OperationId。",
                    "Enter the complete OperationId shown at confirmation."));
            return;
        }
        try
        {
            var result = await ViewModel.AgentConnection.SendAsync(
                new QueryAgentRealOperationRequest(new OperationId(parsed), CorrelationId.New()),
                CancellationToken.None);
            if (!result.IsSuccess || result.Value is not AgentRealOperationResponse current)
            {
                PublishOperationResult(result.Status, result.Messages, result.CorrelationId,
                    Text("真实状态查询失败", "Real status query failed"), "real");
                return;
            }
            var steps = string.Join(Environment.NewLine, current.Steps.Select(step =>
                $"{step.StepId}: {step.State} ({step.Code ?? "-"})"));
            await ShowMessageAsync(Text("真实操作状态", "Real operation status"),
                $"OperationId: {current.Plan.OperationId.Value}\n" +
                $"Plan hash: {current.Plan.PlanHash}\n" +
                $"{Text("状态", "State")}: {current.State}\n" +
                $"{Text("需要对账", "Requires reconciliation")}: {current.RequiresReconciliation}\n" +
                $"{Text("代码", "Code")}: {current.Code ?? "-"}\n{steps}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishOperationException(Text("真实状态查询失败", "Real status query failed"),
                "real", exception, "real.query.failed");
        }
    }

    protected async Task StopRealOperationFollowingStepsByIdAsync()
    {
        if (ViewModel.AgentConnection is null)
            return;
        var raw = await PromptAsync(Text("停止后续步骤", "Stop following steps"), string.Empty);
        if (raw is null)
            return;
        if (!Guid.TryParse(raw, out var parsed) || parsed == Guid.Empty)
        {
            await ShowMessageAsync(Text("操作 ID 无效", "Invalid operation ID"),
                Text("请输入确认时显示的完整 OperationId。",
                    "Enter the complete OperationId shown at confirmation."));
            return;
        }

        var flow = new RealOperationStopSession(
            ViewModel.AgentConnection, ViewModel.RealProductSessionId);
        try
        {
            var queried = await flow.QueryAsync(new OperationId(parsed), CancellationToken.None);
            if (!queried.IsSuccess || queried.Value is not AgentRealOperationResponse observed)
            {
                PublishOperationResult(queried.Status, queried.Messages, queried.CorrelationId,
                    Text("真实状态查询失败", "Real status query failed"), "real");
                return;
            }
            if (observed.State is not (RealOperationState.Prepared
                or RealOperationState.Accepted or RealOperationState.Running))
            {
                await ShowMessageAsync(Text("没有可停止的后续步骤", "No following steps to stop"),
                    $"OperationId: {observed.Plan.OperationId.Value}\n" +
                    $"{Text("状态", "State")}: {observed.State}\n" +
                    Text("请按操作 ID 查询和核对当前结果。",
                        "Query and reconcile the current result by OperationId."));
                return;
            }

            var warning = $"OperationId: {observed.Plan.OperationId.Value}\n" +
                $"Plan hash: {observed.Plan.PlanHash}\n" +
                $"{Text("当前状态", "Current state")}: {observed.State}\n" +
                Text("停止请求只阻止尚未开始的后续步骤；正在执行的 Windows 调用可能继续，已完成的步骤不会回滚。随后请按 ID 查询持久化结果。",
                    "The stop request prevents later steps from starting. A Windows call already in progress may continue, and completed steps are not rolled back. Query the persisted result by ID afterward.");
            var confirmed = await ConfirmAsync(
                Text("确认停止后续步骤", "Confirm stop of following steps"), warning);
            var stopTask = flow.StopAfterCurrentStepAsync(
                observed, confirmed, CancellationToken.None);
            if (stopTask is null)
                return;
            var result = await stopTask;
            if (!result.IsSuccess || result.Value is not AgentRealOperationResponse status)
            {
                PublishOperationResult(result.Status, result.Messages, result.CorrelationId,
                    Text("停止请求未确认", "Stop request was not confirmed"), "real");
                return;
            }
            await ShowMessageAsync(Text("已请求停止后续步骤", "Following-step stop requested"),
                $"OperationId: {status.Plan.OperationId.Value}\n" +
                $"{Text("当前状态", "Current state")}: {status.State}\n" +
                Text("当前调用可能继续；请稍后查询并核对最终状态。",
                    "The current call may continue. Query and reconcile the final state later."));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishOperationException(Text("停止结果待核对", "Stop result needs review"),
                "real", exception, "real.stop.uncertain");
        }
    }

    /// <summary>Target text accompanies user-visible operation feedback.</summary>
    protected string OperationTarget => ViewModel.IsUsingSimulatedInventory
        ? Text($"模拟目标：{ViewModel.SelectedSystem.DisplayName}",
            $"Simulated target: {ViewModel.SelectedSystem.DisplayName}")
        : Text($"本机目标：{ViewModel.SelectedSystem.DisplayName}",
            $"Local target: {ViewModel.SelectedSystem.DisplayName}");

    protected void PublishOperationException(
        string title,
        string source,
        Exception exception,
        string code,
        bool showNotification = true)
    {
        PublishOperationFeedback(
            GlobalNotificationSeverity.Error,
            title,
            Text(
                "操作未完成。请检查当前选择、连接或权限后重试。",
                "The operation did not complete. Check the current selection, connection, or permissions, then try again."),
            source,
            code,
            $"{exception.GetType().Name}: {exception.Message}",
            showNotification);
    }

    protected void PublishOperationResult(
        ApplicationStatus status,
        IReadOnlyList<ApplicationMessage> messages,
        CorrelationId correlationId,
        string title,
        string source,
        bool showNotification = true)
    {
        var first = messages.FirstOrDefault();
        var outcomeUnknown = status == ApplicationStatus.OutcomeUnknown;
        var message = outcomeUnknown
            ? Text(
                "操作结果尚未确认。请刷新当前模拟状态后再决定是否重试。",
                "The operation outcome is not confirmed. Refresh the current simulation state before deciding whether to retry.")
            : Text(
                "操作未完成。请检查当前选择或条件后重试。",
                "The operation did not complete. Check the current selection or conditions, then try again.");
        var detail = first is null
            ? $"status={status}; correlation={correlationId.Value}"
            : $"status={status}; correlation={correlationId.Value}; {first.Code}: {first.DiagnosticText}";
        PublishOperationFeedback(
            outcomeUnknown ? GlobalNotificationSeverity.Warning : GlobalNotificationSeverity.Error,
            title,
            message,
            source,
            first?.Code is { Length: > 0 } value ? value : $"{source}.{status}",
            detail,
            showNotification,
            occurrenceKey: outcomeUnknown ? $"{source}:outcome-unknown:{correlationId.Value}" : null);
    }

    protected void PublishOperationFeedback(
        GlobalNotificationSeverity severity,
        string title,
        string message,
        string source,
        string code,
        string? detail = null,
        bool showNotification = true,
        string? occurrenceKey = null)
    {
        ViewModel.NotificationService.Publish(
            severity,
            title,
            $"{OperationTarget}{Environment.NewLine}{message}",
            source,
            new GlobalNotificationOptions
            {
                OccurrenceKey = occurrenceKey,
                ShowNotification = showNotification,
                RecordInHistory = true,
                Code = code,
                SystemId = ViewModel.SelectedSystem.Id,
                Target = OperationTarget,
                Detail = detail ?? string.Empty
            });
    }
}
