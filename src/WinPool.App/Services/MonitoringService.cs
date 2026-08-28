using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.Monitoring;

namespace WinPool.App.Services;

public sealed record MonitorSamplePoint(
    DateTimeOffset Timestamp,
    double ActivityPercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double VirtualDiskActiveBytes = 0,
    double VirtualDiskMissingBytes = 0,
    double VirtualDiskStaleBytes = 0,
    double VirtualDiskNeedRegenerationBytes = 0,
    double VirtualDiskRegeneratingBytes = 0,
    double VirtualDiskPendingDeletionBytes = 0)
{
    public double VirtualDiskProblemBytes =>
        MissingOrZero(VirtualDiskMissingBytes) +
        MissingOrZero(VirtualDiskStaleBytes) +
        MissingOrZero(VirtualDiskNeedRegenerationBytes) +
        MissingOrZero(VirtualDiskRegeneratingBytes) +
        MissingOrZero(VirtualDiskPendingDeletionBytes);

    private static double MissingOrZero(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;
}

public sealed class MonitoringService : IDisposable
{
    public static readonly double[] RateOptions = [0.2, 0.5, 1, 2, 5, 10, 20];

    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly IAgentConnection? _agentConnection;
    private readonly Dictionary<string, List<MonitorSamplePoint>> _windows = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loopCts;
    private DiskPerformanceSampler? _sampler;
    private StreamWriter? _csvWriter;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _disposed;
    private SessionId? _remoteSessionId;
    private readonly Dictionary<string, DateTimeOffset> _remoteLastTimestamps =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<StorageHealthEvent> _recentStorageHealthEvents = [];
    private readonly SamplingDiagnosticsTracker _diagnostics = new();
    private MonitorRuntimeDiagnostics _agentDiagnostics = new(0, 0);
    private int _restartGeneration;

    public MonitoringService(IAgentConnection? agentConnection = null)
    {
        _agentConnection = agentConnection;
    }

    public bool UsesAgent => _agentConnection is not null;

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public double SampleRateHz { get; private set; } = 5;

    public string? SessionFilePath { get; private set; }

    public static int? ParseDiskNumber(string instanceName)
    {
        var end = instanceName.IndexOf(' ');
        var digits = end < 0 ? instanceName : instanceName[..end];
        return int.TryParse(digits, out var number) ? number : null;
    }

    public async Task<bool> StartAsync(
        double rateHz,
        CancellationToken cancellationToken = default)
    {
        SampleRateHz = rateHz;
        if (IsRunning)
        {
            return true;
        }

        if (_agentConnection is not null)
        {
            LastError = "agent.monitor-starting";
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            var connection = await _agentConnection.ConnectAsync(startupTimeout.Token);
            if (!connection.IsSuccess)
            {
                LastError = connection.Messages.FirstOrDefault()?.DiagnosticText
                    ?? connection.Messages.FirstOrDefault()?.Code
                    ?? "agent.connect-failed";
                IsRunning = false;
                return false;
            }

            IsRunning = true;
            LastError = null;
            _loopCts = new CancellationTokenSource();
            var ready = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartRemoteAsync(rateHz, _loopCts.Token, ready);
            try
            {
                var started = await ready.Task.WaitAsync(startupTimeout.Token);
                return started;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                IsRunning = false;
                LastError = "agent.monitor-start-timeout";
                _loopCts.Cancel();
                ready.TrySetResult(false);
                return false;
            }
        }

        _sampler ??= new DiskPerformanceSampler();
        LastError = null;
        StartSessionFile();
        IsRunning = true;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
            _ = Task.Run(() => RunLoopAsync(token));
        return true;
    }

    public void Start(double rateHz) => _ = StartAsync(rateHz);

    public void SetRate(double rateHz) => _ = SetRateAsync(rateHz);

    public async Task SetRateAsync(double rateHz)
    {
        rateHz = Math.Clamp(rateHz, 0.2, 20);
        SampleRateHz = rateHz;
        if (_agentConnection is not null && IsRunning)
        {
            await RestartRemoteAsync(rateHz);
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _loopCts?.Cancel();
        if (_agentConnection is not null)
        {
            if (_remoteSessionId is { } sessionId)
            {
                await _agentConnection.SendAsync(
                    new StopAgentMonitoringRequest(
                        sessionId,
                        CorrelationId.New()),
                    CancellationToken.None);
            }

            _remoteSessionId = null;
            lock (_sync)
            {
                _windows.Clear();
                _remoteLastTimestamps.Clear();
            }
            return;
        }

        try
        {
            if (_csvWriter is not null)
            {
                await _csvWriter.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        lock (_sync)
        {
            _csvWriter?.Dispose();
            _csvWriter = null;
            _windows.Clear();
        }
    }

    public IReadOnlyDictionary<string, MonitorSamplePoint[]> GetWindows()
    {
        lock (_sync)
        {
            return _windows.ToDictionary(
                x => x.Key,
                x => x.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyDictionary<string, MonitorSamplePoint> GetLatest()
    {
        lock (_sync)
        {
            return _windows
                .Where(x => x.Value.Count > 0)
                .ToDictionary(
                    x => x.Key,
                    x => x.Value[^1],
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<StorageHealthEvent> GetRecentStorageHealthEvents()
    {
        lock (_sync)
        {
            return _recentStorageHealthEvents.ToArray();
        }
    }

    public SamplingDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            return _diagnostics.Snapshot(
                _windows.Values.Sum(points => points.Count),
                _agentDiagnostics);
        }
    }

    public async Task FlushAsync()
    {
        if (_agentConnection is not null)
        {
            if (_remoteSessionId is not null)
            {
                await RefreshRemoteSnapshotAsync(CancellationToken.None);
            }

            return;
        }

        try
        {
            StreamWriter? writer;
            lock (_sync)
            {
                writer = _csvWriter;
            }
            if (writer is not null)
            {
                await writer.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    public async Task<bool> ExportCsvAsync(
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (_agentConnection is not null)
        {
            if (_remoteSessionId is not { } sessionId)
            {
                return false;
            }

            var result = await _agentConnection.SendAsync(
                new ExportAgentMonitorCsvRequest(
                    sessionId,
                    destinationPath,
                    overwrite,
                    CorrelationId.New()),
                cancellationToken);
            return result.IsSuccess
                   && result.Value is ExportArtifactResponse;
        }

        if (SessionFilePath is null || !File.Exists(SessionFilePath))
        {
            return false;
        }

        File.Copy(SessionFilePath, destinationPath, overwrite);
        return true;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            await RunLoopCoreAsync(token);
        }
        catch (Exception ex)
        {
            try
            {
                DiagnosticLog.AppendFailure(
                    StorageDataLocations.CurrentRoot,
                    "monitor.jsonl",
                    "MonitorLoop",
                    ex);
            }
            catch
            {
            }
        }
    }

    public Task DetachAsync()
    {
        _loopCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task StartRemoteAsync(
        double rateHz,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> ready)
    {
        var signaled = false;
        try
        {
            var existing = await _agentConnection!.SendAsync(
                new GetAgentSnapshotRequest(CorrelationId.New()),
                cancellationToken);
            if (existing.IsSuccess
                && existing.Value is AgentSnapshotResponse existingSnapshot
                && existingSnapshot.Snapshot.ActiveMonitoringSession is { } active)
            {
                var existingRate = 1 / Math.Max(active.Request.SamplingInterval.TotalSeconds, 0.001);
                if (Math.Abs(existingRate - rateHz) < 0.05)
                {
                    _remoteSessionId = active.SessionId;
                    SampleRateHz = existingRate;
                    await RefreshRemoteSnapshotAsync(cancellationToken);
                    signaled = true;
                    ready.TrySetResult(true);
                    await RunRemotePollLoopAsync(cancellationToken);
                    return;
                }

                await _agentConnection.SendAsync(
                    new StopAgentMonitoringRequest(
                        active.SessionId,
                        CorrelationId.New()),
                    cancellationToken);
            }

            var systemId = SystemId.New();
            var sessionId = SessionId.New();
            var request = new MonitorRequest(
                sessionId,
                systemId,
                [
                    new MonitorTarget(
                        new StorageObjectId(
                            systemId,
                            StorageObjectKind.PhysicalDisk,
                            "pdh-wildcard"),
                        "*"),
                    new MonitorTarget(
                        new StorageObjectId(
                            systemId,
                            StorageObjectKind.VirtualDisk,
                            "pdh-storage-spaces-wildcard"),
                        "*")
                ],
                [
                    MonitorMetricKind.ActiveTimePercent,
                    MonitorMetricKind.ReadBytesPerSecond,
                    MonitorMetricKind.WriteBytesPerSecond,
                    MonitorMetricKind.AverageQueueLength,
                    MonitorMetricKind.VirtualDiskActiveBytes,
                    MonitorMetricKind.VirtualDiskMissingBytes,
                    MonitorMetricKind.VirtualDiskStaleBytes,
                    MonitorMetricKind.VirtualDiskNeedRegenerationBytes,
                    MonitorMetricKind.VirtualDiskRegeneratingBytes,
                    MonitorMetricKind.VirtualDiskPendingDeletionBytes
                ],
                TimeSpan.FromSeconds(1 / Math.Clamp(rateHz, 0.2, 20)),
                ContinueWhenUiCloses: true);
            var response = await _agentConnection.SendAsync(
                new StartAgentMonitoringRequest(request, CorrelationId.New()),
                cancellationToken);
            if (!response.IsSuccess)
            {
                IsRunning = false;
                var details = string.Join(
                    " | ",
                    response.Messages.Select(message =>
                        string.IsNullOrWhiteSpace(message.DiagnosticText)
                            ? message.Code
                            : $"{message.Code}: {message.DiagnosticText}"));
                LastError = string.IsNullOrWhiteSpace(details)
                    ? $"agent.monitor-start-failed ({response.Status})"
                    : $"{response.Status}: {details}";
                ready.TrySetResult(false);
                return;
            }

            _remoteSessionId = sessionId;
            signaled = true;
            ready.TrySetResult(true);
            await RunRemotePollLoopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!signaled)
            {
                if (_loopCts is null || cancellationToken == _loopCts.Token)
                {
                    IsRunning = false;
                }

                LastError = "agent.monitor-start-cancelled";
                ready.TrySetResult(false);
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            IsRunning = false;
            LastError = $"agent.monitor-start-{ex.GetType().Name}";
            ready.TrySetResult(false);
        }
    }

    private async Task RestartRemoteAsync(double rateHz)
    {
        var generation = Interlocked.Increment(ref _restartGeneration);
        _loopCts?.Cancel();
        var previousSession = _remoteSessionId;
        _remoteSessionId = null;
        if (previousSession is { } sessionId && _agentConnection is not null)
        {
            await _agentConnection.SendAsync(
                new StopAgentMonitoringRequest(
                    sessionId,
                    CorrelationId.New()),
                CancellationToken.None);
        }

        if (generation != Volatile.Read(ref _restartGeneration))
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var ready = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = StartRemoteAsync(rateHz, _loopCts.Token, ready);
        try
        {
            var started = await ready.Task;
            if (!started && generation == Volatile.Read(ref _restartGeneration))
            {
                LastError ??= "agent.monitor-restart-failed";
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _restartGeneration))
            {
                LastError = $"agent.monitor-restart-{exception.GetType().Name}";
            }
        }
    }

    private async Task RunRemotePollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(
                Math.Clamp(1000.0 / Math.Max(0.2, SampleRateHz), 50, 1000)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshRemoteSnapshotAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or TimeoutException
                    or InvalidOperationException)
            {
                RecordSamplingFailure($"agent.{exception.GetType().Name}");
            }
        }
    }

    private async Task RefreshRemoteSnapshotAsync(CancellationToken cancellationToken)
    {
        var response = await _agentConnection!.SendAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()),
            cancellationToken);
        if (!response.IsSuccess
            || response.Value is not AgentSnapshotResponse snapshot
            || snapshot.Snapshot.ActiveMonitoringSession is not { } session)
        {
            RecordSamplingFailure(
                response.Messages.FirstOrDefault()?.Code
                ?? "agent.snapshot-unavailable");
            return;
        }

        _remoteSessionId = session.SessionId;
        var cutoff = DateTimeOffset.UtcNow - WindowLength;
        lock (_sync)
        {
            _recentStorageHealthEvents =
                snapshot.Snapshot.RecentStorageHealthEvents?.ToArray() ?? [];
            _agentDiagnostics =
                snapshot.Snapshot.MonitorDiagnostics ?? new MonitorRuntimeDiagnostics(0, 0);
            foreach (var sample in snapshot.Snapshot.LatestMonitorSamples ?? [])
            {
                var instance = sample.TargetId.ProviderKey.StartsWith(
                        "pdh-storage-spaces:",
                        StringComparison.OrdinalIgnoreCase)
                    ? $"Storage Space: {sample.TargetId.ProviderKey[19..]}"
                    : sample.TargetId.ProviderKey.StartsWith(
                        "pdh:",
                        StringComparison.OrdinalIgnoreCase)
                        ? sample.TargetId.ProviderKey[4..]
                        : sample.TargetId.ProviderKey;
                if (_remoteLastTimestamps.TryGetValue(instance, out var previous)
                    && sample.SampledAtUtc <= previous)
                {
                    continue;
                }

                _remoteLastTimestamps[instance] = sample.SampledAtUtc;
                if (!_windows.TryGetValue(instance, out var points))
                {
                    points = [];
                    _windows[instance] = points;
                }

                points.Add(
                    new MonitorSamplePoint(
                        sample.SampledAtUtc,
                        Metric(sample, MonitorMetricKind.ActiveTimePercent),
                        Metric(sample, MonitorMetricKind.ReadBytesPerSecond),
                        Metric(sample, MonitorMetricKind.WriteBytesPerSecond),
                        Metric(sample, MonitorMetricKind.VirtualDiskActiveBytes),
                        Metric(sample, MonitorMetricKind.VirtualDiskMissingBytes),
                        Metric(sample, MonitorMetricKind.VirtualDiskStaleBytes),
                        Metric(sample, MonitorMetricKind.VirtualDiskNeedRegenerationBytes),
                        Metric(sample, MonitorMetricKind.VirtualDiskRegeneratingBytes),
                        Metric(sample, MonitorMetricKind.VirtualDiskPendingDeletionBytes)));
                points.RemoveAll(point => point.Timestamp < cutoff);
            }

            RecordSamplingSuccessLocked(
                snapshot.Snapshot.LatestMonitorSamples?
                    .Select(sample => (DateTimeOffset?)sample.SampledAtUtc)
                    .Max()
                ?? DateTimeOffset.UtcNow);
        }
    }

    private static double Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.FirstOrDefault(value => value.Kind == kind)?.Value ?? 0d;

    private void RecordSamplingFailure(string code)
    {
        lock (_sync)
        {
            _diagnostics.RecordFailure(code);
        }
    }

    private void RecordSamplingSuccessLocked(DateTimeOffset sampledAtUtc)
    {
        _diagnostics.RecordSuccess(sampledAtUtc);
    }

    private async Task RunLoopCoreAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(1 / Math.Clamp(SampleRateHz, 0.2, 20));
            try
            {
                await Task.Delay(interval, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            IReadOnlyList<DiskPerformanceSample> samples;
            try
            {
                samples = _sampler?.Sample() ?? [];
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                RecordSamplingFailure($"local.{ex.GetType().Name}");
                continue;
            }

            var now = DateTimeOffset.Now;
            lock (_sync)
            {
                RecordSamplingSuccessLocked(now);
            }
            var rows = new StringBuilder();
            lock (_sync)
            {
                foreach (var sample in samples)
                {
                    if (!_windows.TryGetValue(sample.InstanceName, out var points))
                    {
                        points = [];
                        _windows[sample.InstanceName] = points;
                    }

                    points.Add(new MonitorSamplePoint(
                        now,
                        sample.ActivityPercent,
                        sample.ReadBytesPerSecond,
                        sample.WriteBytesPerSecond));
                    var cutoff = now - WindowLength;
                    var removeCount = points.Count(x => x.Timestamp < cutoff);
                    if (removeCount > 0)
                    {
                        points.RemoveRange(0, removeCount);
                    }

                    rows.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',')
                        .Append(sample.InstanceName.Replace(',', ' ')).Append(',')
                        .Append(sample.ActivityPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(sample.ReadBytesPerSecond.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(sample.WriteBytesPerSecond.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }

                try
                {
                    _csvWriter?.Write(rows.ToString());
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            }

            if (now - _lastFlush > TimeSpan.FromSeconds(2))
            {
                _lastFlush = now;
                await FlushAsync();
            }
        }
    }

    private void StartSessionFile()
    {
        try
        {
            var directory = Path.Combine(StorageDataLocations.CurrentRoot, "Monitoring");
            Directory.CreateDirectory(directory);
            SessionFilePath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var stream = new FileStream(
                SessionFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            _csvWriter = new StreamWriter(stream, new UTF8Encoding(true));
            _csvWriter.WriteLine("Timestamp,Disk,ActivityPercent,ReadBytesPerSecond,WriteBytesPerSecond");
            _lastFlush = DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _csvWriter = null;
            SessionFilePath = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loopCts?.Cancel();
        if (_agentConnection is not null)
        {
            return;
        }
        lock (_sync)
        {
            _csvWriter?.Dispose();
            _csvWriter = null;
        }
        _sampler?.Dispose();
        _sampler = null;
    }
}
