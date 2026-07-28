using System.Text;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

public sealed record MonitorSamplePoint(
    DateTimeOffset Timestamp,
    double ActivityPercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond);

public sealed class MonitoringService : IDisposable
{
    public static readonly double[] RateOptions = [0.2, 0.5, 1, 2, 5, 10, 20];

    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly Dictionary<string, List<MonitorSamplePoint>> _windows = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loopCts;
    private DiskPerformanceSampler? _sampler;
    private StreamWriter? _csvWriter;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public bool BackgroundEnabled { get; set; }

    public double SampleRateHz { get; private set; } = 1;

    public string? SessionFilePath { get; private set; }

    public volatile bool WindowMinimized;

    public static int? ParseDiskNumber(string instanceName)
    {
        var end = instanceName.IndexOf(' ');
        var digits = end < 0 ? instanceName : instanceName[..end];
        return int.TryParse(digits, out var number) ? number : null;
    }

    public void Start(double rateHz)
    {
        SampleRateHz = rateHz;
        if (IsRunning)
        {
            return;
        }

        _sampler ??= new DiskPerformanceSampler();
        StartSessionFile();
        IsRunning = true;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _ = Task.Run(() => RunLoopAsync(token));
    }

    public void SetRate(double rateHz) => SampleRateHz = rateHz;

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _loopCts?.Cancel();
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

    public async Task FlushAsync()
    {
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
                var directory = StorageDataLocations.CurrentRoot;
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "monitor-debug.log"),
                    $"{DateTime.Now:O} [MonitorLoop] {ex}\n\n");
            }
            catch
            {
            }
        }
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

            if (!BackgroundEnabled && WindowMinimized)
            {
                continue;
            }

            IReadOnlyList<DiskPerformanceSample> samples;
            try
            {
                samples = _sampler?.Sample() ?? [];
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            var now = DateTimeOffset.Now;
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
        lock (_sync)
        {
            _csvWriter?.Dispose();
            _csvWriter = null;
        }
        _sampler?.Dispose();
        _sampler = null;
    }
}
