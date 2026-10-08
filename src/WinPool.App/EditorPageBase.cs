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
public partial class EditorPageBase : Page
{
    public EditorPageBase()
    {
        Loaded += (_, _) =>
        {
            if (ViewModel is null) return;
            ViewModel.RealOperationActivityChanged -= SyncRealOperationActivity;
            ViewModel.RealOperationActivityChanged += SyncRealOperationActivity;
            SyncRealOperationActivity();
        };
        Unloaded += (_, _) =>
        {
            if (ViewModel is not null) ViewModel.RealOperationActivityChanged -= SyncRealOperationActivity;
        };
    }

    private void SyncRealOperationActivity() => OnRealOperationActivityChanged(
        ViewModel.IsRealOperationBusy, ViewModel.IsRealGraphObscured, ViewModel.RealOperationPhase);

    protected virtual void OnRealOperationActivityChanged(bool isBusy, bool showOverlay, string phase) { }
    protected bool IsRealStructureApplyInProgress { get; private set; }
    protected AgentRealOperationResponse? LastRealOperationResponse { get; private set; }
    private bool structureHasWritten;
    private readonly string realProgressKey = "real:editor:" + Guid.NewGuid().ToString("N");

    private void ReportRealActivity(string phase, bool overlay = false)
    {
        ViewModel.SetRealOperationActivity(true, overlay || structureHasWritten, phase);
        ViewModel.NotificationService.Publish(GlobalNotificationSeverity.Info,
            Text("真实磁盘操作", "Real storage operation"), phase, "real",
            new GlobalNotificationOptions { OccurrenceKey = realProgressKey, IsProgress = true,
                AutoDismiss = false, RecordInHistory = false });
    }

    private void EndRealActivity()
    {
        if (IsRealStructureApplyInProgress) return;
        ViewModel.NotificationService.DismissByKey(realProgressKey);
        ViewModel.SetRealOperationActivity(false, false, string.Empty);
    }
    private static readonly Guid MicrosoftReservedPartitionType =
        new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    protected WorkspaceViewModel ViewModel { get; set; } = null!;
    protected SimulationEditingSession EditingSession { get; } = new();
    protected StorageSnapshot _working { get => EditingSession.Working; set => EditingSession.Working = value; }

    protected const double MinTopologyWidth = 320;
    protected const double TopologyWidthMargin = 20;
    protected const long BytesPerMiB = 1024L * 1024;

    protected long UnallocatedIgnoreBytes =>
        Math.Max(0, ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);

    protected string Text(string zh, string en) =>
        ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? zh : en;

    /// <summary>
    /// Called after a completed real operation has produced a fresh local
    /// inventory snapshot. Editor pages rebind their view state here while
    /// preserving any selection whose stable object still exists.
    /// </summary>
    protected virtual void OnRealInventoryRefreshed()
    {
    }

    internal void RefreshActiveRealInventoryFromWorkspace()
    {
        if (!ViewModel.IsUsingSimulatedInventory)
            OnRealInventoryRefreshed();
    }

    protected bool LastRealInventoryRefreshSucceeded { get; private set; }

    protected static bool IsExactProviderMsr(PartitionInfo partition) =>
        partition.IsStable
        && partition.Type.Equals("MicrosoftReserved", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(partition.PartitionTypeId, out var typeId)
        && typeId == MicrosoftReservedPartitionType;

    protected async Task<bool> SubmitInitializedDiskLayoutAsync(
        OsDiskInfo disk, bool createMsr, long? virtualDiskSizeBytes = null,
        bool formatNtfs = false, string? label = null, char? letter = null)
    {
        if (!string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync(Text("布局计划被阻止", "Layout plan blocked"),
                Text("新扫描中的目标磁盘不是 GPT；未提交后续布局计划。",
                    "The target disk in the fresh scan is not GPT. No follow-up layout plan was submitted."));
            return false;
        }

        var partitions = _working.Partitions.Where(partition =>
            string.Equals(partition.OsDiskStableId, disk.StableId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partitions.Any(partition => !IsExactProviderMsr(partition))
            || partitions.Length > 1)
        {
            await ShowMessageAsync(Text("布局计划被阻止", "Layout plan blocked"),
                Text("新扫描发现 MSR 以外的分区或多个 MSR。请先核对实际布局；未提交后续布局计划。",
                    "The fresh scan found a non-MSR partition or multiple MSRs. Review the actual layout first; no follow-up layout plan was submitted."));
            return false;
        }

        var system = ViewModel.ActiveDocument.SystemId;
        var diskId = new StorageObjectId(system, StorageObjectKind.OsDisk, disk.StableId);
        StorageObjectId? msrId = partitions.Length == 0
            ? null
            : new StorageObjectId(system, StorageObjectKind.Partition, partitions[0].StableId);
        try
        {
            var proposal = RealOperationProposalFactory.ConfigureInitializedDisk(
                system, diskId, msrId, createMsr, virtualDiskSizeBytes,
                formatNtfs, label, letter);
            return proposal is null || await SubmitRealAsync(proposal);
        }
        catch (ArgumentException exception)
        {
            await ShowMessageAsync(Text("布局参数不受支持", "Layout parameters are unsupported"),
                exception.Message);
            return false;
        }
    }

    private async Task RefreshRealInventoryAsync(StorageInventoryScope? scope = null)
    {
        LastRealInventoryRefreshSucceeded = false;
        var previousSnapshot = ViewModel.EffectiveActiveSnapshot;
        if (scope is null)
            await ViewModel.ScanAsync();
        else
        {
            if (!await ViewModel.RefreshRealOperationScopeAsync(scope)) return;
            // The observer may already have applied this exact complete batch before its reply arrived.
            LastRealInventoryRefreshSucceeded = true;
            OnRealInventoryRefreshed();
            if (App.Window is MainWindow scopedWindow)
                scopedWindow.RefreshActiveEditorAfterRealInventory(this);
            return;
        }
        var refreshed = ViewModel.EffectiveActiveSnapshot;
        if (!ReferenceEquals(refreshed, previousSnapshot)
            && string.IsNullOrWhiteSpace(ViewModel.ScanError))
        {
            LastRealInventoryRefreshSucceeded = true;
            OnRealInventoryRefreshed();
            if (App.Window is MainWindow mainWindow)
                mainWindow.RefreshActiveEditorAfterRealInventory(this);
        }
    }

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

        if (!ViewModel.RealOperationSubmission.TryReservePreparation())
            return false;
        var reservationId = ViewModel.RealOperationSubmission.ReservationId;
        LastRealOperationResponse = null;
        LastRealInventoryRefreshSucceeded = false;
        ReportRealActivity(Text("正在准备准确计划", "Preparing the exact plan"));
        try
        {
            return await SubmitReservedRealAsync(intent, ViewModel.AgentConnection, ViewModel.RealProductSessionId);
        }
        finally
        {
            ViewModel.RealOperationSubmission.ReleasePreparation(reservationId);
            EndRealActivity();
        }
    }

    private async Task<bool> SubmitReservedRealAsync(
        RealOperationIntentRequest intent, IAgentConnection connection, string productSessionId)
    {
        var systemId = ViewModel.ActiveDocument.SystemId;
        var flow = new RealOperationRequestSession(connection, productSessionId);
        ApplicationResult<AgentResponse>? prepared = null;
        try
        {
            prepared = await flow.PrepareAsync(intent, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
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
            {
                PublishOperationResult(prepared.Status, prepared.Messages, prepared.CorrelationId,
                    Text("真实准备未完成", "Real preparation did not complete"), "real");
                var reason = prepared.Messages.FirstOrDefault();
                var detail = $"{Text("状态", "Status")}: {prepared.Status}\n" +
                    $"{Text("代码", "Code")}: {reason?.Code ?? "agent.real_operation.unexpected_response"}\n" +
                    $"CorrelationId: {prepared.CorrelationId.Value}";
                if (!string.IsNullOrWhiteSpace(reason?.DiagnosticText))
                    detail += $"\n{Text("诊断", "Diagnostic")}: {reason.DiagnosticText}";
                await ShowMessageAsync(Text("真实准备未完成", "Real preparation did not complete"), detail);
            }
            return false;
        }

        var plan = frozen.Plan;
        StorageInventoryScope? refreshScope = null;
        if (ViewModel.ActiveDocument.SourceFacts is { } sourceFacts)
        {
            var anchors = plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
            if (anchors.Length > 0)
                refreshScope = StorageInventoryScopeFactory.Create(sourceFacts, plan.OperationId, null, anchors);
        }
        if (!ViewModel.RealOperationSubmission.TrackPrepared(frozen))
        {
            await CancelPreparedRealAsync(connection, productSessionId, frozen);
            return false;
        }
        var confirmation = RealOperationConfirmationFormatter.Format(plan,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn);
        ReportRealActivity(Text("等待确认准确计划", "Waiting for exact-plan confirmation"));
        if (!await ConfirmAsync(Text("确认真实磁盘写入", "Confirm real disk write"), confirmation)
            || !ViewModel.IsRealMode || !ViewModel.IsLocalSystem
            || ViewModel.ActiveDocument.SystemId != systemId)
        {
            await CancelPreparedRealAsync(connection, productSessionId, frozen);
            return false;
        }
        flow.Confirm(frozen);
        ReportRealActivity(Text("正在接受并执行", "Accepting and executing"));

        ApplicationResult<AgentResponse>? accepted = null;
        try
        {
            accepted = await flow.AcceptOnceAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            PublishOperationException(Text("提交结果待核对", "Submission needs reconciliation"),
                "real", exception, "real.accept.uncertain");
        }

        if (accepted is { IsSuccess: false } && accepted.Status != ApplicationStatus.OutcomeUnknown)
        {
            PublishOperationResult(accepted.Status, accepted.Messages, accepted.CorrelationId,
                Text("真实提交被拒绝", "Real submission was rejected"), "real");
            await CancelPreparedRealAsync(connection, productSessionId, frozen);
            return false;
        }
        if (accepted is { IsSuccess: true, Value: AgentRealOperationResponse acceptedStatus })
            ObserveFrozenRealStatus(plan, acceptedStatus);

        // Accept is short-lived. Query the assigned identity after a lost reply;
        // never issue a second write request to infer whether the first ran.
        // Each storage step includes fresh provider and safety reads. A normal
        // multi-step pool operation can take longer than thirty seconds; keep
        // querying the same accepted identity so its separately confirmed
        // follow-up layout is still offered when completion is observed.
        for (var attempt = 0; attempt < 1200; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            ApplicationResult<AgentResponse> status;
            try
            {
                status = await flow.QueryAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                PublishOperationException(Text("真实状态查询失败", "Real status query failed"),
                    "real", exception, "real.query.failed");
                return false;
            }
            if (!status.IsSuccess || status.Value is not AgentRealOperationResponse current)
                continue;
            if (!ObserveFrozenRealStatus(plan, current))
                continue;
            LastRealOperationResponse = current;
            var hasWritten = current.Steps.Any(step => step.State is RealOperationStepState.CallIssued
                or RealOperationStepState.WaitingForProvider or RealOperationStepState.Verifying
                or RealOperationStepState.Verified or RealOperationStepState.OutcomeUnknown);
            if (IsRealStructureApplyInProgress && hasWritten) structureHasWritten = true;
            ReportRealActivity(Text("正在执行 / 核对", "Executing / verifying") + ": "
                + string.Join(", ", current.Steps.Select(step => $"{step.StepId}: {step.State}")), hasWritten);
            if (current.State is RealOperationState.Accepted or RealOperationState.Running)
                continue;
            var succeeded = current.State == RealOperationState.Succeeded && !current.RequiresReconciliation;
            if (succeeded || hasWritten)
            {
                ReportRealActivity(Text("正在刷新相关对象", "Refreshing related objects"), hasWritten);
                await RefreshRealInventoryAsync(refreshScope);
            }
            var observedNoEffect = current.State == RealOperationState.Failed
                && current.Code == "real.reconciliation_observed_tiered_creation_no_effect";
            PublishOperationFeedback(succeeded ? GlobalNotificationSeverity.Info : GlobalNotificationSeverity.Warning,
                succeeded ? Text("真实操作完成", "Real operation completed")
                    : observedNoEffect ? Text("创建失败", "Creation failed")
                    : Text("真实操作需要核对", "Real operation needs review"),
                observedNoEffect
                    ? Text("Windows 拒绝了此容量或布局，已核实没有创建新对象。请调整目标后重新应用。",
                        "Windows rejected this capacity or layout. No new objects were observed. Adjust the target and apply again.")
                    : $"{current.State}: {current.Code ?? "-"}", "real", current.Code ?? "real.completed",
                $"OperationId: {plan.OperationId.Value}");
            return succeeded && LastRealInventoryRefreshSucceeded;
        }

        await ShowMessageAsync(Text("真实操作继续执行", "Real operation continues"),
            $"OperationId: {plan.OperationId.Value}\n" +
            Text("请稍后按此 OperationId 查询持久化状态。",
                "Query this OperationId later for its persisted status."));
        return false;
    }

    private bool ObserveFrozenRealStatus(OperationPlan plan, AgentRealOperationResponse response)
    {
        if (response.Plan.OperationId != plan.OperationId
            || !StringComparer.Ordinal.Equals(response.Plan.PlanHash, plan.PlanHash)) return false;
        ViewModel.ObserveRealOperation(response);
        return true;
    }

    private async Task CancelPreparedRealAsync(
        IAgentConnection connection, string productSessionId, AgentRealOperationResponse frozen)
    {
        try
        {
            var result = await connection.SendAsync(new StopAgentRealOperationFollowingStepsRequest(
                frozen.Plan.OperationId, frozen.Plan.PlanHash, productSessionId,
                CorrelationId.New()), CancellationToken.None);
            if (result.IsSuccess && result.Value is AgentRealOperationResponse current
                && ObserveFrozenRealStatus(frozen.Plan, current)
                && !ViewModel.RealOperationSubmission.IsBlocked)
                return;
            // Mode exit may already have cancelled this plan. Only a read-only
            // query for the same frozen identity can prove that after a lost
            // or rejected stop reply.
            result = await connection.SendAsync(new QueryAgentRealOperationRequest(
                frozen.Plan.OperationId, CorrelationId.New()), CancellationToken.None);
            if (result.IsSuccess && result.Value is AgentRealOperationResponse queried
                && ObserveFrozenRealStatus(frozen.Plan, queried)
                && !ViewModel.RealOperationSubmission.IsBlocked)
                return;
            PublishOperationResult(result.Status, result.Messages, result.CorrelationId,
                Text("取消准备结果待核对", "Prepared cancellation needs review"), "real");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            PublishOperationException(Text("取消准备结果待核对", "Prepared cancellation needs review"),
                "real", exception, "real.cancel_prepared.uncertain");
        }
    }

    protected async Task QueryRealOperationByIdAsync()
    {
        if (ViewModel.AgentConnection is null)
            return;
        var raw = ViewModel.RealOperationSubmission.OperationId?.Value.ToString()
            ?? ViewModel.LastRealOperationStatus?.Plan.OperationId.Value.ToString()
            ?? await PromptAsync(Text("查询真实操作", "Query real operation"), string.Empty);
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
            if (current.Plan.OperationId.Value != parsed)
                throw new InvalidOperationException("The Agent response did not match the queried operation.");
            ViewModel.ObserveRealOperation(current);
            if (current.State is not (RealOperationState.Prepared or RealOperationState.Accepted or RealOperationState.Running)
                && ViewModel.ActiveDocument.SourceFacts is { } facts)
            {
                var anchors = current.Plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
                if (anchors.Length > 0)
                    await RefreshRealInventoryAsync(StorageInventoryScopeFactory.Create(facts,
                        current.Plan.OperationId, "query-result", anchors));
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            PublishOperationException(Text("真实状态查询失败", "Real status query failed"),
                "real", exception, "real.query.failed");
        }
    }

    protected async Task StopRealOperationFollowingStepsByIdAsync()
    {
        if (ViewModel.AgentConnection is null)
            return;
        var raw = await PromptAsync(Text("停止后续步骤", "Stop following steps"),
            ViewModel.RealOperationSubmission.OperationId?.Value.ToString() ?? string.Empty);
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
            if (observed.Plan.OperationId.Value != parsed)
                throw new InvalidOperationException("The Agent response did not match the queried operation.");
            ViewModel.RealOperationSubmission.Observe(observed);
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
            if (status.Plan.OperationId != observed.Plan.OperationId
                || !StringComparer.Ordinal.Equals(status.Plan.PlanHash, observed.Plan.PlanHash))
                throw new InvalidOperationException("The Agent response did not match the stopped operation.");
            ViewModel.RealOperationSubmission.Observe(status);
            await ShowMessageAsync(Text("已请求停止后续步骤", "Following-step stop requested"),
                $"OperationId: {status.Plan.OperationId.Value}\n" +
                $"{Text("当前状态", "Current state")}: {status.State}\n" +
                Text("当前调用可能继续；请稍后查询并核对最终状态。",
                    "The current call may continue. Query and reconcile the final state later."));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
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
