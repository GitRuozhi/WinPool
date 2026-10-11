using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

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
            await ShowMessageAsync(Text("后续分区设置已停止", "Further partition setup stopped"),
                Text("新扫描中的目标磁盘不是 GPT；未继续执行分区变更。",
                    "The target disk in the fresh scan is not GPT. No further partition changes were made."));
            return false;
        }

        var partitions = _working.Partitions.Where(partition =>
            string.Equals(partition.OsDiskStableId, disk.StableId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partitions.Any(partition => !IsExactProviderMsr(partition))
            || partitions.Length > 1)
        {
            await ShowMessageAsync(Text("后续分区设置已停止", "Further partition setup stopped"),
                Text("新扫描发现 MSR 以外的分区或多个 MSR。请先核对实际布局；未继续执行分区变更。",
                    "The fresh scan found a non-MSR partition or multiple MSRs. Review the actual layout first; no further partition changes were made."));
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

    protected Task<bool> SubmitRealAsync(RealOperationIntentRequest intent) => SubmitRealAsync(intent, null, null);

    private async Task<bool> SubmitRealAsync(RealOperationIntentRequest intent,
        RealStructureApplyAuthorization? authorization, Action? validateApplyContext)
    {
        if (!ViewModel.CanSubmitRealOperation || ViewModel.AgentConnection is null)
            return false;

        try
        {
            RealOperationValidator.Validate(intent);
            validateApplyContext?.Invoke();
            authorization?.ValidateProposal(intent);
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
        ReportRealActivity(Text("正在核对目标和操作条件", "Checking the target and operation conditions"));
        try
        {
            return await SubmitReservedRealAsync(intent, ViewModel.AgentConnection, ViewModel.RealProductSessionId,
                authorization, validateApplyContext);
        }
        finally
        {
            ViewModel.RealOperationSubmission.ReleasePreparation(reservationId);
            EndRealActivity();
        }
    }

    private async Task<bool> SubmitReservedRealAsync(
        RealOperationIntentRequest intent, IAgentConnection connection, string productSessionId,
        RealStructureApplyAuthorization? authorization, Action? validateApplyContext)
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
            PublishOperationException(Text("操作未能开始", "Operation did not start"),
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
                    Text("操作未能开始", "Operation did not start"), "real");
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
        try
        {
            if (authorization is not null)
            {
                // Each fresh frozen segment must fit the one confirmed structural goal.
                // This does not bypass Agent preparation, token issuance or live preflight.
                validateApplyContext!.Invoke();
                authorization.ValidateFrozen(intent, frozen);
            }
            else
            {
                var confirmation = RealOperationConfirmationFormatter.Format(plan,
                    ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn,
                    ViewModel.ActiveDocument.SourceFacts);
                ReportRealActivity(Text("等待确认应用", "Waiting for apply confirmation"));
                if (!await ConfirmAsync(Text("确认真实磁盘写入", "Confirm real disk write"), confirmation))
                {
                    await CancelPreparedRealAsync(connection, productSessionId, frozen);
                    return false;
                }
            }
            if (!ViewModel.IsRealMode || !ViewModel.IsLocalSystem || ViewModel.ActiveDocument.SystemId != systemId
                || ViewModel.RealProductSessionId != productSessionId)
            {
                await CancelPreparedRealAsync(connection, productSessionId, frozen);
                return false;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            await CancelPreparedRealAsync(connection, productSessionId, frozen);
            throw;
        }
        flow.Confirm(frozen);
        ReportRealActivity(Text("正在应用", "Applying"));

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
                PublishOperationException(Text("无法读取操作结果", "Could not read the operation result"),
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
            ReportRealActivity(Text("正在执行 / 核对", "Executing / verifying"), hasWritten);
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
                    : succeeded
                        ? Text("已完成应用并核对结果。", "The changes were applied and verified.")
                        : current.RequiresReconciliation
                            ? RealOperationFeedback.ReconciliationReason(current,
                                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn)
                            : Text("操作未完成。相关结果见消息，详细原因见开发页消息详情。",
                                "The operation did not complete. Review the outcome in messages and the diagnostic details on the developer page."),
                "real", current.Code ?? "real.completed",
                RealOperationFeedback.Recovery(current,
                    ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn,
                    ViewModel.ActiveDocument.SourceFacts).Options.Detail);
            return succeeded && LastRealInventoryRefreshSucceeded;
        }

        PublishOperationFeedback(GlobalNotificationSeverity.Warning,
            Text("真实操作结果尚未确认", "Real operation outcome is not confirmed"),
            Text("当前尚未收到可核实的完成结果。相关状态见消息，详细原因见开发页消息详情。",
                "A verifiable completion result has not been received. Review the status in messages and the diagnostic details on the developer page."),
            "real", "real.query.wait_expired", $"OperationId: {plan.OperationId.Value}");
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
                Text("操作取消结果待核对", "Operation cancellation needs review"), "real");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            PublishOperationException(Text("操作取消结果待核对", "Operation cancellation needs review"),
                "real", exception, "real.cancel_prepared.uncertain");
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
            ? source == "real"
                ? Text("操作结果尚未确认，暂时不能继续写入。相关结果见消息，详细原因见开发页消息详情。",
                    "The operation outcome is not confirmed and further writes are blocked. Review the outcome in messages and the diagnostic details on the developer page.")
                : Text(
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
