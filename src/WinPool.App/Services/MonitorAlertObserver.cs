using Microsoft.UI.Dispatching;
using WinPool.Application;
using WinPool.App.ViewModels;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

/// <summary>
/// Keeps monitor issue and storage-health notifications tied to the App
/// lifetime, rather than to MonitorPage navigation.
/// </summary>
internal sealed class MonitorAlertObserver : IDisposable
{
    private const string StartFailureKey = "monitor.start.failed";
    private const int RememberedStorageEventKeys = 128;

    private readonly WorkspaceViewModel _viewModel;
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _publishedStorageEventKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _storageEventKeyOrder = new();
    private DateTimeOffset _storageEventCutoff = DateTimeOffset.UtcNow;
    private bool _hasAuthoritativeAgentSnapshot;
    private bool _startFailureBlocksCurrentPreference;
    private bool _tickInProgress;
    private bool _started;
    private bool _disposed;
    private Task? _activeTick;

    public MonitorAlertObserver(
        WorkspaceViewModel viewModel,
        DispatcherQueue dispatcherQueue)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        _started = true;
        _timer.Start();
        BeginTick();
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args) => BeginTick();

    private void BeginTick()
    {
        if (!_disposed && !_tickInProgress)
        {
            _activeTick = TickAsync();
        }
    }

    private async Task TickAsync()
    {
        if (_disposed || _tickInProgress)
        {
            return;
        }

        _tickInProgress = true;
        try
        {
            var monitoring = _viewModel.Monitoring;
            if (monitoring.UsesAgent)
            {
                await monitoring.RefreshRemoteStateAsync(_shutdown.Token);
                if (_disposed)
                {
                    return;
                }

                _hasAuthoritativeAgentSnapshot |= monitoring.IsRemoteStateKnown;
            }

            if (!monitoring.UsesAgent
                || (monitoring.IsRemoteStateKnown && _viewModel.AgentPreferencesLoaded))
            {
                await SynchronizeMonitoringAsync();
                if (_disposed)
                {
                    return;
                }
            }

            if (_disposed)
            {
                return;
            }

            UpdateIssueStates();
            PublishNewStorageHealthEvents();
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                LogFailure("AlertObserver", exception);
            }
        }
        finally
        {
            _tickInProgress = false;
        }
    }

    private async Task SynchronizeMonitoringAsync()
    {
        var monitoring = _viewModel.Monitoring;
        var preferences = _viewModel.CurrentAgentPreferences;
        if (_disposed)
        {
            return;
        }

        if (!preferences.ContinuousMonitoringEnabled)
        {
            _startFailureBlocksCurrentPreference = false;
            if (monitoring.IsRunning)
            {
                await monitoring.StopAsync();
                if (_disposed)
                {
                    return;
                }
            }

            return;
        }

        var rateHz = Math.Clamp(preferences.MonitoringSampleRateHz, 0.2, 20);
        if (Math.Abs(monitoring.SampleRateHz - rateHz) >= 0.000_001)
        {
            await monitoring.SetRateAsync(rateHz);
            if (_disposed)
            {
                return;
            }
        }

        if (monitoring.IsRunning && monitoring.HasPollingLoop)
        {
            return;
        }

        if (_startFailureBlocksCurrentPreference)
        {
            return;
        }

        var started = await monitoring.StartAsync(rateHz, _shutdown.Token);
        if (_disposed)
        {
            return;
        }

        if (started)
        {
            _startFailureBlocksCurrentPreference = false;
            _viewModel.NotificationService.ResolveByKey(StartFailureKey);
            return;
        }

        _startFailureBlocksCurrentPreference = true;
        PublishStartFailure(monitoring.LastError);
        try
        {
            // Match the Monitor page's previous behavior: a failed automatic
            // start disables the preference so the App does not retry forever.
            await _viewModel.SetContinuousMonitoringAsync(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            LogFailure("DisableAfterStartFailure", exception);
        }
    }

    private void UpdateIssueStates()
    {
        var monitoring = _viewModel.Monitoring;
        var zh = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var diagnostics = monitoring.GetDiagnostics();
        var runtime = monitoring.GetRuntimeDiagnostics();
        var issues = new List<MonitorIssueState>();

        if (runtime.PersistencePaused)
        {
            issues.Add(new(
                $"persistence-paused:{runtime.PersistenceFailureOccurrenceId ?? runtime.SessionOccurrenceId ?? "current"}",
                zh
                    ? $"⛔ 记录已暂停：{runtime.PersistenceFailure ?? "正在等待安全恢复"}"
                    : $"⛔ Recording paused: {runtime.PersistenceFailure ?? "waiting for safe recovery"}"));
        }

        var knownLoss = SaturatingAdd(
            Math.Max(0, runtime.ConfirmedLostSamples),
            Math.Max(0, diagnostics.PersistenceDroppedSamples));
        if (knownLoss > 0)
        {
            issues.Add(new(
                $"known-loss:{runtime.SessionOccurrenceId ?? "terminal"}",
                zh
                    ? $"⚠ 记录不完整：{knownLoss} 条样本未保存"
                    : $"⚠ Incomplete recording: {knownLoss} samples were not saved"));
        }

        if (runtime.PersistenceDelayed && !runtime.PersistencePaused)
        {
            var samplingConfirmed = monitoring.IsRunning
                && monitoring.IsRemoteStateKnown
                && string.IsNullOrWhiteSpace(runtime.SamplingFailure);
            issues.Add(new(
                $"persistence-delay:{runtime.SessionOccurrenceId ?? "current"}",
                samplingConfirmed
                    ? zh
                        ? $"⚠ 写入延迟：采样继续，{runtime.PendingPersistenceSamples} 条样本暂存内存"
                        : $"⚠ Write delayed: sampling continues; {runtime.PendingPersistenceSamples} samples are buffered in memory"
                    : zh
                        ? $"⚠ 写入延迟：{runtime.PendingPersistenceSamples} 条样本待写入"
                        : $"⚠ Write delayed: {runtime.PendingPersistenceSamples} samples are pending persistence"));
        }

        if (!string.IsNullOrWhiteSpace(runtime.PersistenceFailure) && !runtime.PersistencePaused)
        {
            issues.Add(new(
                $"persistence-failure:{runtime.PersistenceFailureOccurrenceId ?? runtime.SessionOccurrenceId ?? "current"}",
                zh
                    ? $"⚠ 记录失败：{runtime.PersistenceFailure}"
                    : $"⚠ Recording failure: {runtime.PersistenceFailure}"));
        }

        if (!string.IsNullOrWhiteSpace(runtime.ArchiveFailure))
        {
            issues.Add(new(
                $"archive:{runtime.ArchiveFailureOccurrenceId ?? "current"}",
                runtime.ArchiveRawDatabaseRetained
                    ? zh
                        ? $"⚠ 归档失败：原始数据库已保留。{runtime.ArchiveFailure}"
                        : $"⚠ Archive failed: the raw database was retained. {runtime.ArchiveFailure}"
                    : zh
                        ? $"⚠ 归档校验失败：请保留现有归档并修复。{runtime.ArchiveFailure}"
                        : $"⚠ Archive verification failed: retain the existing archive and repair it. {runtime.ArchiveFailure}"));
        }

        if (!string.IsNullOrWhiteSpace(runtime.SamplingFailure))
        {
            issues.Add(new(
                $"sampling:{runtime.SessionOccurrenceId ?? "current"}",
                zh
                    ? $"⚠ 采样已停止：{runtime.SamplingFailure}"
                    : $"⚠ Sampling stopped: {runtime.SamplingFailure}"));
        }

        if (runtime.HasRecoveredInterruptedSession)
        {
            issues.Add(new(
                $"interrupted-session:{runtime.RecoveredInterruptedSessionOccurrenceId ?? "current"}",
                zh
                    ? "⚠ 上次监控异常结束：可能有数量未知的未保存样本"
                    : "⚠ Previous monitoring ended unexpectedly: an unknown number of samples may not have been saved"));
        }

        if (diagnostics.ActiveSubscribers > 0
            && diagnostics.SubscriberCapacity > 0
            && (long)diagnostics.SubscriberBufferedSamples * 4
                >= (long)diagnostics.SubscriberCapacity * 3)
        {
            issues.Add(new(
                $"display-delay:{runtime.SessionOccurrenceId ?? "current"}",
                zh
                    ? "⚠ 实时显示延迟：部分实时图表样本未显示"
                    : "⚠ Live display delayed: some chart samples were not displayed"));
        }

        if (diagnostics.ConsecutiveFailures > 0
            && (!monitoring.UsesAgent || _hasAuthoritativeAgentSnapshot))
        {
            var failureCode = diagnostics.LastFailureCode ?? "unknown";
            issues.Add(new(
                $"communication:{failureCode}",
                zh
                    ? $"⚠ 通信或采样异常：{failureCode}"
                    : $"⚠ Communication or sampling issue: {failureCode}"));
        }
        else if ((!monitoring.UsesAgent || monitoring.IsRemoteStateKnown)
                 && !string.IsNullOrWhiteSpace(monitoring.LastError)
                 && !string.Equals(monitoring.LastError, runtime.PersistenceFailure, StringComparison.Ordinal)
                 && !string.Equals(monitoring.LastError, runtime.ArchiveFailure, StringComparison.Ordinal)
                 && !string.Equals(monitoring.LastError, runtime.SamplingFailure, StringComparison.Ordinal))
        {
            issues.Add(new(
                $"control:{monitoring.IsRunning}:{monitoring.LastError}",
                monitoring.IsRunning
                    ? zh
                        ? $"⚠ 监控控制异常：{monitoring.LastError}"
                        : $"⚠ Monitoring control issue: {monitoring.LastError}"
                    : zh
                        ? $"⚠ 监控停止原因：{monitoring.LastError}"
                        : $"⚠ Monitoring stopped: {monitoring.LastError}"));
        }

        var stateIsAuthoritative = !monitoring.UsesAgent || monitoring.IsRemoteStateKnown;
        var snapshot = monitoring.UpdateIssueStates(
            issues.Select(issue => issue with
            {
                IsPermanentGap = issue.Key.StartsWith("known-loss:", StringComparison.Ordinal)
                    || issue.Key.StartsWith("interrupted-session:", StringComparison.Ordinal)
            }),
            stateIsAuthoritative);
        PublishIssueTransitions(snapshot, zh);
    }

    private void PublishIssueTransitions(MonitorIssueStateSnapshot snapshot, bool zh)
    {
        foreach (var transition in snapshot.Transitions)
        {
            var issue = transition.Issue;
            var occurrenceKey = $"monitor-issue:{issue.Key}";
            if (transition.Kind == MonitorIssueTransitionKind.Appeared)
            {
                _viewModel.NotificationService.Publish(
                    GlobalNotificationSeverity.Warning,
                    zh ? "监控异常" : "Monitoring issue",
                    $"{MonitorTarget(zh)}{Environment.NewLine}{issue.Text}",
                    "monitor",
                    new GlobalNotificationOptions
                    {
                        OccurrenceKey = occurrenceKey,
                        Code = "monitor.issue",
                        SystemId = MonitorSystemId,
                        Target = MonitorTarget(zh),
                        Detail = issue.Text
                    });
                continue;
            }

            _viewModel.NotificationService.ResolveByKey(occurrenceKey);
            _viewModel.NotificationService.Publish(
                GlobalNotificationSeverity.Info,
                zh ? "监控异常已恢复" : "Monitoring issue recovered",
                $"{MonitorTarget(zh)}{Environment.NewLine}{(zh ? "已恢复：" : "Recovered: ")}{issue.Text}",
                "monitor",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = $"monitor-recovered:{issue.Key}",
                    ShowNotification = false,
                    RecordInHistory = true,
                    Code = "monitor.recovered",
                    SystemId = MonitorSystemId,
                    Target = MonitorTarget(zh),
                    Detail = issue.Text
                });
        }
    }

    private void PublishNewStorageHealthEvents()
    {
        var events = _viewModel.Monitoring.GetRecentStorageHealthEvents()
            .Where(item => item.OccurredAtUtc >= _storageEventCutoff)
            .OrderBy(item => item.OccurredAtUtc)
            .ToArray();
        foreach (var item in events)
        {
            var occurrenceKey = $"storage-event:{item.Channel}:{item.RecordId}:{item.EventId}";
            if (!RememberStorageEventKey(occurrenceKey))
            {
                continue;
            }

            if (item.Severity is not (StorageHealthEventSeverity.Critical
                or StorageHealthEventSeverity.Error
                or StorageHealthEventSeverity.Warning))
            {
                continue;
            }

            var zh = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
            var summary = $"{item.Provider} · Event {item.EventId} · {item.Severity}";
            _viewModel.NotificationService.Publish(
                item.Severity is StorageHealthEventSeverity.Critical or StorageHealthEventSeverity.Error
                    ? GlobalNotificationSeverity.Error
                    : GlobalNotificationSeverity.Warning,
                zh ? "存储健康事件" : "Storage health event",
                $"{MonitorTarget(zh)}{Environment.NewLine}{summary}",
                "monitor",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = occurrenceKey,
                    Code = $"storage-event.{item.EventId}",
                    SystemId = MonitorSystemId,
                    Target = MonitorTarget(zh),
                    Detail = item.Message
                });
        }

        if (events.Length > 0)
        {
            _storageEventCutoff = events[^1].OccurredAtUtc;
        }
    }

    private bool RememberStorageEventKey(string key)
    {
        if (!_publishedStorageEventKeys.Add(key))
        {
            return false;
        }

        _storageEventKeyOrder.Enqueue(key);
        while (_storageEventKeyOrder.Count > RememberedStorageEventKeys)
        {
            _publishedStorageEventKeys.Remove(_storageEventKeyOrder.Dequeue());
        }

        return true;
    }

    private void PublishStartFailure(string? detail)
    {
        var zh = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        _viewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Warning,
            _viewModel.Localization["MonitorIntro"],
            $"{MonitorTarget(zh)}{Environment.NewLine}{(zh ? "监控未能启动或状态尚未确认。请检查 Agent 连接后重试。" : "Monitoring did not start or its state is not confirmed. Check the Agent connection, then try again.")}",
            "monitor",
            new GlobalNotificationOptions
            {
                OccurrenceKey = StartFailureKey,
                Code = StartFailureKey,
                SystemId = MonitorSystemId,
                Target = MonitorTarget(zh),
                Detail = detail ?? string.Empty
            });
    }

    private string MonitorSystemId => _viewModel.SystemCatalog.Systems
        .First(system => system.IsLocal)
        .Id;

    private string MonitorTarget(bool zh)
    {
        var local = _viewModel.SystemCatalog.Systems.First(system => system.IsLocal);
        return zh
            ? $"本机监控目标：{local.DisplayName}"
            : $"Local monitoring target: {local.DisplayName}";
    }

    private static long SaturatingAdd(long first, long second) =>
        first > long.MaxValue - second ? long.MaxValue : first + second;

    private static void LogFailure(string source, Exception exception)
    {
        try
        {
            DiagnosticLog.AppendFailure(
                StorageDataLocations.CurrentRoot,
                "monitor.jsonl",
                source,
                exception);
        }
        catch (Exception loggingException) when (
            loggingException is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _shutdown.Cancel();
    }

    public async Task StopAsync()
    {
        Dispose();
        var activeTick = _activeTick;
        if (activeTick is not null)
        {
            await activeTick;
        }

        _shutdown.Dispose();
    }
}
