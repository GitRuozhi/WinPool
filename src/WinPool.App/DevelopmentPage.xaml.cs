using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using CoreLanguagePreference = WinPool.Domain.LanguagePreference;

namespace WinPool_App;

public sealed partial class DevelopmentPage : Page
{
    private WorkspaceViewModel ViewModel { get; set; } = null!;
    private readonly ObservableCollection<string> _lines = [];
    private readonly ObservableCollection<string> _runtimeLines = [];
    private readonly ObservableCollection<string> _planLines = [];
    private readonly ObservableCollection<string> _algorithmLines = [];
    private readonly ObservableCollection<string> _eventLines = [];
    private CancellationTokenSource? _eventCancellation;

    public DevelopmentPage()
    {
        InitializeComponent();
        LogItems.ItemsSource = _lines;
        RuntimeItems.ItemsSource = _runtimeLines;
        PlanItems.ItemsSource = _planLines;
        AlgorithmItems.ItemsSource = _algorithmLines;
        EventItems.ItemsSource = _eventLines;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        var zh = ViewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        DevelopmentIntro.Text = ViewModel.Localization["DevelopmentIntro"];
        ClearButton.Content = zh ? "清空事件/旧日志" : "Clear events / legacy log";
        RefreshDiagnosticsButton.Content = zh ? "刷新诊断" : "Refresh diagnostics";
        CompareInventoryButton.Content = zh
            ? "对照原生与脚本采集"
            : "Compare native and script inventory";
        RuntimeTab.Header = zh ? "运行时" : "Runtime";
        PlansTab.Header = zh ? "计划与步骤" : "Plans and steps";
        AlgorithmsTab.Header = zh ? "算法目录" : "Algorithms";
        EventsTab.Header = zh ? "Application 事件" : "Application events";
        LegacyLogTab.Header = zh ? "旧命令日志" : "Legacy command log";
        RebuildLegacyLog();
        ((INotifyCollectionChanged)ViewModel.CommandLog.Entries).CollectionChanged += Entries_CollectionChanged;
        _eventCancellation = new CancellationTokenSource();
        _ = RefreshDiagnosticsAsync(_eventCancellation.Token);
        _ = WatchAgentEventsAsync(_eventCancellation.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _eventCancellation?.Cancel();
        _eventCancellation?.Dispose();
        _eventCancellation = null;
        ((INotifyCollectionChanged)ViewModel.CommandLog.Entries).CollectionChanged -= Entries_CollectionChanged;
        base.OnNavigatedFrom(e);
    }

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildLegacyLog();
        DispatcherQueue.TryEnqueue(() =>
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, disableAnimation: true));
    }

    private void RebuildLegacyLog()
    {
        _lines.Clear();
        foreach (var entry in ViewModel.CommandLog.Entries)
        {
            var tag = entry.Simulated ? "[SIM]" : "[REAL]";
            _lines.Add($"{entry.At:HH:mm:ss} {tag} [{entry.Source}] {entry.Command}");
            if (!string.IsNullOrWhiteSpace(entry.Output))
            {
                _lines.Add($"    → {entry.Output}");
            }
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CommandLog.Clear();
        _eventLines.Clear();
    }

    private async void RefreshDiagnosticsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RefreshDiagnosticsAsync(CancellationToken.None);

    private async Task RefreshDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var zh = ViewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        if (ViewModel.AgentConnection is null)
        {
            _runtimeLines.Clear();
            _runtimeLines.Add(zh ? "Agent 连接不可用。" : "Agent connection unavailable.");
            _planLines.Clear();
            _algorithmLines.Clear();
            return;
        }

        RefreshDiagnosticsButton.IsEnabled = false;
        try
        {
            var result = await ViewModel.AgentConnection.SendAsync(
                new GetDevelopmentDiagnosticsRequest(10, CorrelationId.New()),
                cancellationToken);
            if (result.Value is not DevelopmentDiagnosticsResponse response)
            {
                _runtimeLines.Clear();
                _runtimeLines.Add(
                    result.Messages.FirstOrDefault()?.Code
                    ?? (zh ? "无法读取开发诊断。" : "Development diagnostics unavailable."));
                return;
            }

            RenderDiagnostics(response.Diagnostics, zh);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _runtimeLines.Clear();
            _runtimeLines.Add(zh ? "读取开发诊断失败。" : "Development diagnostics failed.");
        }
        finally
        {
            RefreshDiagnosticsButton.IsEnabled = true;
        }
    }

    private void RenderDiagnostics(DevelopmentDiagnostics diagnostics, bool zh)
    {
        _runtimeLines.Clear();
        var agent = diagnostics.Agent;
        _runtimeLines.Add($"Agent {agent.AgentInstanceId.Value:N}");
        _runtimeLines.Add(
            $"Tray={agent.IsTrayVisible}  Shutdown={agent.ShutdownStatus.State}  "
            + $"Monitor={agent.ActiveMonitoringSession?.State.ToString() ?? "None"}  "
            + $"Test={agent.ActiveTestRunId?.Value.ToString("N") ?? "None"}");
        if (agent.MonitorDiagnostics is { } queue)
        {
            _runtimeLines.Add(
                $"MonitorQueue buffered={queue.BufferedSamples} dropped={queue.DroppedSamples} "
                + $"window={queue.WindowDroppedSamples} persistence={queue.PersistenceDroppedSamples} "
                + $"subscriber={queue.SubscriberDroppedSamples} rejected={queue.RejectedSourceSamples}");
            _runtimeLines.Add(
                $"Subscribers active={queue.ActiveSubscribers} buffered={queue.SubscriberBufferedSamples}/"
                + $"{queue.SubscriberCapacity}");
        }
        _runtimeLines.Add(zh ? "受监督进程：" : "Supervised processes:");
        foreach (var process in agent.Processes.OrderBy(item => item.Kind).ThenBy(item => item.ProcessId))
        {
            _runtimeLines.Add(
                $"  {process.Kind} pid={process.ProcessId} state={process.State} "
                + $"job={process.OwnsJobObject} heartbeat={process.LastHeartbeatUtc:O}");
        }

        _planLines.Clear();
        if (diagnostics.RecentPlans.Count == 0)
        {
            _planLines.Add(zh ? "尚无持久化测试计划。" : "No persisted test plans.");
        }
        foreach (var plan in diagnostics.RecentPlans)
        {
            _planLines.Add(
                $"RUN {plan.RunId.Value:N} state={plan.State} created={plan.CreatedAtUtc:O}");
            _planLines.Add($"  hash={plan.PlanHash}");
            _planLines.Add(
                $"  algorithm={plan.PlannerAlgorithm.Id} {plan.PlannerAlgorithm.Version} "
                + $"[{plan.PlannerAlgorithm.Confidence}]");
            foreach (var step in plan.Steps)
            {
                _planLines.Add(
                    $"    {step.StepId} {step.Action} state={step.State} "
                    + $"tool={step.ToolId ?? "internal"} depends=[{string.Join(",", step.DependsOn)}] "
                    + $"parameters=[{string.Join(",", step.ParameterKeys)}]");
            }
        }

        _algorithmLines.Clear();
        foreach (var algorithm in diagnostics.Algorithms)
        {
            var marker = algorithm.Confidence == AlgorithmConfidence.Speculative
                ? (zh ? "【推测，待验证】 " : "[SPECULATIVE, VERIFY] ")
                : string.Empty;
            _algorithmLines.Add(
                $"{marker}{algorithm.Id} {algorithm.Version} [{algorithm.Confidence}] "
                + algorithm.EvidenceReference);
        }
    }

    private async Task WatchAgentEventsAsync(CancellationToken cancellationToken)
    {
        if (ViewModel.AgentConnection is null)
        {
            return;
        }

        try
        {
            await foreach (var agentEvent in ViewModel.AgentConnection
                               .WatchAsync(cancellationToken))
            {
                var line = FormatEvent(agentEvent);
                if (line is null)
                {
                    continue;
                }
                DispatcherQueue.TryEnqueue(() =>
                {
                    while (_eventLines.Count >= 500)
                    {
                        _eventLines.RemoveAt(0);
                    }
                    _eventLines.Add(line);
                    EventScrollViewer.ChangeView(
                        null,
                        EventScrollViewer.ScrollableHeight,
                        null,
                        disableAnimation: true);
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string? FormatEvent(AgentEvent agentEvent)
    {
        var task = agentEvent switch
        {
            AgentTaskEvent item => item.TaskEvent,
            AgentTestEvent item => item.TestEvent.TaskEvent,
            _ => null
        };
        if (task is not null)
        {
            var progress = task.ProgressFraction is { } fraction
                ? $" progress={Math.Clamp(fraction, 0, 1):P1}"
                : string.Empty;
            return $"{task.OccurredAtUtc:HH:mm:ss.fff} {task.Kind}/{task.State} "
                   + $"code={task.Code} step={task.StepId ?? "-"}{progress}";
        }

        return agentEvent switch
        {
            AgentProcessStateEvent process =>
                $"{process.OccurredAtUtc:HH:mm:ss.fff} Process {process.Registration.Kind} "
                + $"pid={process.Registration.ProcessId} state={process.Registration.State}",
            AgentToolStateEvent tool =>
                $"{tool.OccurredAtUtc:HH:mm:ss.fff} Tool {tool.ToolState.ToolId.Value} "
                + $"state={tool.ToolState.Availability}",
            AgentShutdownEvent shutdown =>
                $"{shutdown.OccurredAtUtc:HH:mm:ss.fff} Shutdown reason={shutdown.Reason}",
            AgentEventTransportStateEvent transport =>
                $"{transport.OccurredAtUtc:HH:mm:ss.fff} EventTransport "
                + $"state={transport.State} gap={transport.HasEventGap} "
                + $"code={transport.DiagnosticCode}",
            _ => null
        };
    }

    private async void CompareInventoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var zh = ViewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        if (ViewModel.AgentConnection is null)
        {
            InventoryComparisonStatus.Text = zh
                ? "Agent 连接不可用。"
                : "The Agent connection is unavailable.";
            return;
        }

        CompareInventoryButton.IsEnabled = false;
        InventoryComparisonStatus.Text = zh
            ? "正在执行只读原生采集和固定脚本采集…"
            : "Running the read-only native and fixed-script collectors…";
        var response = await ViewModel.AgentConnection.SendAsync(
            new CaptureAgentInventoryRequest(
                IncludeLegacyComparison: true,
                CorrelationId.New()),
            CancellationToken.None);
        CompareInventoryButton.IsEnabled = true;
        if (response.Value is not InventoryCaptureResponse capture)
        {
            InventoryComparisonStatus.Text =
                response.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "采集对照失败。" : "Inventory comparison failed.");
            return;
        }

        var differenceCount = capture.Comparison?.Differences.Count ?? 0;
        InventoryComparisonStatus.Text = zh
            ? $"原生对象 {capture.NativeSnapshot.Objects.Count}；脚本对象 {capture.LegacySnapshot?.Objects.Count ?? 0}；字段/关系差异 {differenceCount}。快照和脱敏差异已写入 SQLite。"
            : $"Native objects: {capture.NativeSnapshot.Objects.Count}; script objects: {capture.LegacySnapshot?.Objects.Count ?? 0}; field/relationship differences: {differenceCount}. Snapshots and sanitized differences were saved to SQLite.";
    }
}
