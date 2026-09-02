using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinPool_App.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool_App;

public sealed partial class MonitorRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _showInGraph = true;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _pool = string.Empty;

    [ObservableProperty]
    private string _volumes = string.Empty;

    [ObservableProperty]
    private string _media = string.Empty;

    [ObservableProperty]
    private string _capacity = string.Empty;

    [ObservableProperty]
    private string _activityText = string.Empty;

    [ObservableProperty]
    private string _readText = string.Empty;

    [ObservableProperty]
    private string _writeText = string.Empty;

    [ObservableProperty]
    private Color _seriesColor;

    partial void OnSeriesColorChanged(Color value) => OnPropertyChanged(nameof(SeriesColorBrush));

    public Microsoft.UI.Xaml.Media.SolidColorBrush SeriesColorBrush => new(SeriesColor);

    public Color AutoColor { get; set; }

    public string? InstanceName { get; set; }
}

public sealed partial class MonitorPage : Page
{
    private static readonly Color[] SeriesPalette =
    [
        Color.FromArgb(255, 0x00, 0x78, 0xD4),
        Color.FromArgb(255, 0x10, 0x7C, 0x10),
        Color.FromArgb(255, 0xCA, 0x50, 0x10),
        Color.FromArgb(255, 0x74, 0x4D, 0xA9),
        Color.FromArgb(255, 0x00, 0x99, 0xBC),
        Color.FromArgb(255, 0xD1, 0x34, 0x38),
        Color.FromArgb(255, 0xFF, 0xB9, 0x00),
        Color.FromArgb(255, 0x8A, 0x88, 0x81),
        Color.FromArgb(255, 0x00, 0xB7, 0xC3),
        Color.FromArgb(255, 0xE3, 0x00, 0x8C),
        Color.FromArgb(255, 0x00, 0x5A, 0x9E),
        Color.FromArgb(255, 0x8C, 0xBD, 0x18),
        Color.FromArgb(255, 0xA4, 0x26, 0x2C),
        Color.FromArgb(255, 0x87, 0x64, 0xB8),
        Color.FromArgb(255, 0x98, 0x6F, 0x0B),
        Color.FromArgb(255, 0x03, 0x83, 0x87),
        Color.FromArgb(255, 0xF7, 0x63, 0x0C),
        Color.FromArgb(255, 0x6B, 0x7A, 0x00)
    ];

    private readonly ObservableCollection<MonitorRowViewModel> _rows = [];
    private readonly ObservableCollection<string> _storageEventRows = [];
    private readonly Dictionary<string, MonitorRowViewModel> _rowsByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, MonitorRowViewModel> _rowsByDiskNumber = new();
    private DateTimeOffset _storageEventCutoff = DateTimeOffset.UtcNow;
    private WorkspaceViewModel _viewModel = null!;
    private DispatcherQueueTimer? _pollTimer;
    private DispatcherQueueTimer? _preferenceSyncTimer;
    private bool _ready;
    private bool _updatingContinuousMonitoring;
    private bool _applyingSampleRate;

    public MonitorPage()
    {
        InitializeComponent();
        DiskRows.ItemsSource = _rows;
        Unloaded += MonitorPage_Unloaded;
    }

    private void MonitorPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _ready = false;
        _pollTimer?.Stop();
        _pollTimer = null;
        _preferenceSyncTimer?.Stop();
        _preferenceSyncTimer = null;
    }

    private MonitoringService Monitoring => _viewModel.Monitoring;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _viewModel = (WorkspaceViewModel)e.Parameter;
        MonitorRoot.Loaded += MonitorRoot_Loaded;
        try
        {
            await _viewModel.RefreshPreferencesAsync(refreshLocalizedContent: false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            LogMonitorFailure("PreferenceRefresh", exception);
        }
        Guard("UpdateText", UpdateText);
        Guard("BuildRows", BuildRows);
        RestoreExistingGraphSeries();
        Monitoring.SetRate(_viewModel.CurrentPreferences.MonitoringSampleRateHz);
        Guard("PopulateRateOptions", PopulateRateOptions);
        _updatingContinuousMonitoring = true;
        ContinuousMonitoringSwitch.IsOn =
            _viewModel.CurrentPreferences.ContinuousMonitoringEnabled;
        _updatingContinuousMonitoring = false;
        if (_viewModel.CurrentPreferences.ContinuousMonitoringEnabled)
        {
            try
            {
                await App.InitialAgentConnectionTask;
                if (App.InitialAgentWarningPublished)
                {
                    _viewModel.NotificationService.PublishWarning(
                        _viewModel.Localization["MonitorIntro"],
                        Monitoring.LastError ?? "监控 Agent 未能启动。",
                        "monitor",
                        "monitor-start-failed");
                }
                else
                {
                    var started = await Monitoring.StartAsync(SelectedRate());
                    if (!started)
                    {
                        _viewModel.NotificationService.PublishWarning(
                            _viewModel.Localization["MonitorIntro"],
                            Monitoring.LastError ?? "监控 Agent 未能启动。",
                            "monitor",
                            "monitor-start-failed");
                    }
                }
            }
            catch (Exception exception)
            {
                LogMonitorFailure("Start", exception);
                _viewModel.NotificationService.PublishWarning(
                    _viewModel.Localization["MonitorIntro"],
                    exception.Message,
                    "monitor",
                    "monitor-start-exception");
            }
        }
        _pollTimer = DispatcherQueue.CreateTimer();
        ApplyPollInterval();
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
        _preferenceSyncTimer = DispatcherQueue.CreateTimer();
        _preferenceSyncTimer.Interval = TimeSpan.FromMilliseconds(500);
        _preferenceSyncTimer.Tick += PreferenceSyncTimer_Tick;
        _preferenceSyncTimer.Start();
        Poll();
        _ready = true;
    }

    private void RestoreExistingGraphSeries()
    {
        var windows = Monitoring.GetWindows();
        var series = new List<DiskGraphSeries>();
        foreach (var (instance, points) in windows)
        {
            var row = ResolveRow(instance);
            row.InstanceName = instance;
            _rowsByInstance[instance] = row;
            if (points.Length < 2)
            {
                continue;
            }

            series.Add(new DiskGraphSeries
            {
                InstanceName = instance,
                DisplayName = row.Name,
                Color = row.SeriesColor,
                Points = points
            });
        }

        ActivityGraph.SetSeries(series);
    }

    private void ApplyPollInterval()
    {
        if (_pollTimer is not null)
        {
            _pollTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Clamp(1000.0 / Math.Max(0.2, Monitoring.SampleRateHz), 50, 1000));
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ready = false;
        _pollTimer?.Stop();
        _pollTimer = null;
        _preferenceSyncTimer?.Stop();
        _preferenceSyncTimer = null;
        base.OnNavigatedFrom(e);
    }

    private async void PreferenceSyncTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (!_ready || _updatingContinuousMonitoring || _applyingSampleRate)
        {
            return;
        }

        try
        {
            await _viewModel.RefreshPreferencesAsync(refreshLocalizedContent: false);
            var enabled = _viewModel.CurrentPreferences.ContinuousMonitoringEnabled;
            _updatingContinuousMonitoring = true;
            ContinuousMonitoringSwitch.IsOn = enabled;
            _updatingContinuousMonitoring = false;
            if (enabled && !Monitoring.IsRunning)
            {
                await Monitoring.StartAsync(SelectedRate());
            }
            else if (!enabled && Monitoring.IsRunning)
            {
                await Monitoring.StopAsync();
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {
            LogMonitorFailure("PreferenceSync", exception);
        }
    }

    private void UpdateText()
    {
        var l = _viewModel.Localization;
        HeaderColor.Text = l["ColorColumn"];
        HeaderName.Text = l["Name"];
        HeaderPool.Text = l["Pool"];
        HeaderVolumes.Text = l["VolumeColumn"];
        HeaderMedia.Text = l["Media"];
        HeaderCapacity.Text = l["Capacity"];
        HeaderActivity.Text = l["Activity"];
        HeaderRead.Text = l["ReadSpeed"];
        HeaderWrite.Text = l["WriteSpeed"];
        ContinuousMonitoringLabel.Text = l["ContinuousMonitoring"];
        SamplingRateLabel.Text = l["SamplingRate"];
        ((TextBlock)((StackPanel)AutoColorsButton.Content).Children[1]).Text = l["AutoColor"];
        ((TextBlock)((StackPanel)ExportButton.Content).Children[1]).Text = l["ExportData"];
        ToolTipService.SetToolTip(RateOptions, l["RefreshRate"]);
        EventsButtonText.Text = l["MonitoringEvents"];
    }

    private void ColorSwatch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Guard("ColorSwatch", () => ColorSwatchCore(sender));
    }

    private void ColorSwatchCore(object sender)
    {
        if (sender is not Button { DataContext: MonitorRowViewModel row })
        {
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var i = 0; i < 6; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (var i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var input = new TextBox
        {
            Width = 130,
            Text = $"#{row.SeriesColor.R:X2}{row.SeriesColor.G:X2}{row.SeriesColor.B:X2}"
        };
        var sorted = SeriesPalette
            .Select((color, index) => (color, hue: ColorHue(color), index))
            .OrderBy(x => x.hue)
            .ThenBy(x => x.index)
            .Select(x => x.color)
            .ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            var color = sorted[i];
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
            };
            ToolTipService.SetToolTip(swatch, hex);
            swatch.Click += (_, _) =>
            {
                input.Text = hex;
                row.SeriesColor = color;
            };
            Grid.SetRow(swatch, i / 6);
            Grid.SetColumn(swatch, i % 6);
            grid.Children.Add(swatch);
        }
        panel.Children.Add(grid);

        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                ApplyColorInput(row, input);
            }
        };
        input.LostFocus += (_, _) => ApplyColorInput(row, input);
        panel.Children.Add(input);

        _colorFlyout = new Flyout { Content = panel };
        _colorFlyout.ShowAt((FrameworkElement)sender);
    }

    private void ApplyColorInput(MonitorRowViewModel row, TextBox input)
    {
        if (TryParseColor(input.Text, out var color))
        {
            row.SeriesColor = color;
            input.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        DismissColorFlyout();
    }

    private static double ColorHue(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta < 0.0001)
        {
            return 360;
        }

        var hue = max == r
            ? 60 * (((g - b) / delta) % 6)
            : max == g
                ? 60 * (((b - r) / delta) + 2)
                : 60 * (((r - g) / delta) + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        var value = text.Trim().TrimStart('#');
        if (value.Length == 6
            && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            color = Color.FromArgb(255, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
            return true;
        }

        var parts = value.Split(',');
        if (parts.Length == 3
            && byte.TryParse(parts[0].Trim(), out var r)
            && byte.TryParse(parts[1].Trim(), out var g)
            && byte.TryParse(parts[2].Trim(), out var b))
        {
            color = Color.FromArgb(255, r, g, b);
            return true;
        }
        return false;
    }

    private Flyout? _colorFlyout;

    private void DismissColorFlyout()
    {
        _colorFlyout?.Hide();
        _colorFlyout = null;
    }

    private void AutoColors_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Guard("AutoColors", () =>
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                _rows[i].SeriesColor = SeriesPalette[i % SeriesPalette.Length];
            }
        });
    }

    private bool _splitApplied;

    private void MonitorRoot_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        MonitorRoot.Loaded -= MonitorRoot_Loaded;
        if (_splitApplied)
        {
            return;
        }

        Guard("AutoSplit", () =>
        {
            var total = MonitorRoot.ActualHeight;
            if (total <= 0)
            {
                return;
            }

            var available = total - ButtonsCard.ActualHeight - 12 - 8;
            if (available < 320)
            {
                return;
            }

            var desiredTable = 40 + (_rows.Count * 34) + 28;
            var table = Math.Min(desiredTable, available * 0.4);
            table = Math.Clamp(table, 140, available - 160);
            GraphRow.Height = new GridLength(1, GridUnitType.Star);
            TableRow.Height = new GridLength(table, GridUnitType.Pixel);
            _splitApplied = true;
        });
    }

    private void TableScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = TableScroll.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        TableHeader.Width = width;
        DiskRows.Width = width;
    }

    private void PopulateRateOptions()
    {
        RateOptions.ItemsSource = MonitoringService.RateOptions
            .Select(x => $"{x:0.#} Hz")
            .ToArray();
        RateOptions.SelectedIndex = IndexOfRate(Monitoring.SampleRateHz);
    }

    private static int IndexOfRate(double rateHz)
    {
        var best = 4;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < MonitoringService.RateOptions.Length; i++)
        {
            var diff = Math.Abs(MonitoringService.RateOptions[i] - rateHz);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }

        return best;
    }

    private double SelectedRate() =>
        RateOptions.SelectedIndex >= 0 && RateOptions.SelectedIndex < MonitoringService.RateOptions.Length
            ? MonitoringService.RateOptions[RateOptions.SelectedIndex]
            : 5;

    private void BuildRows()
    {
        _rows.Clear();
        _rowsByInstance.Clear();
        _rowsByDiskNumber.Clear();
        var snapshot = _viewModel.Snapshot;
        foreach (var disk in snapshot.OsDisks.OrderBy(x => x.Number))
        {
            var physical = disk.PhysicalDiskStableId is null
                ? null
                : snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == disk.PhysicalDiskStableId);
            var virtualDisk = disk.VirtualDiskStableId is null
                ? null
                : snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == disk.VirtualDiskStableId);
            var poolId = physical?.PoolStableId ?? virtualDisk?.PoolStableId;
            var pool = poolId is null
                ? string.Empty
                : snapshot.StoragePools.FirstOrDefault(x => x.StableId == poolId && !x.IsPrimordial)?.FriendlyName
                  ?? string.Empty;
            var volumes = string.Join(
                " ",
                snapshot.Partitions
                    .Where(x => x.OsDiskStableId == disk.StableId && !string.IsNullOrWhiteSpace(x.DriveLetter))
                    .OrderBy(x => x.PartitionNumber)
                    .Select(x => x.DriveLetter + ":"));
            AddRow(
                disk.FriendlyName,
                pool,
                volumes,
                physical?.MediaType ?? (virtualDisk is null ? string.Empty : _viewModel.Localization["VirtualDisk"]),
                FormatCapacity(disk.Size),
                disk.Number,
                !string.IsNullOrWhiteSpace(volumes));
        }
    }

    private MonitorRowViewModel AddRow(
        string name,
        string pool,
        string volumes,
        string media,
        string capacity,
        int? diskNumber,
        bool showInGraph)
    {
        var row = new MonitorRowViewModel
        {
            Name = name,
            Pool = pool,
            Volumes = volumes,
            Media = media,
            Capacity = capacity,
            ShowInGraph = showInGraph,
            AutoColor = SeriesPalette[_rows.Count % SeriesPalette.Length]
        };
        row.SeriesColor = row.AutoColor;
        _rows.Add(row);
        if (diskNumber is not null)
        {
            _rowsByDiskNumber[diskNumber.Value] = row;
        }
        return row;
    }

    private void Poll()
    {
        try
        {
            PollCore();
        }
        catch (Exception ex)
        {
            LogMonitorFailure("Poll", ex);
        }
    }

    internal static void LogMonitorFailure(string source, Exception ex)
    {
        try
        {
            DiagnosticLog.AppendFailure(
                WinPool.Infrastructure.Windows.StorageDataLocations.CurrentRoot,
                "monitor.jsonl",
                source,
                ex);
        }
        catch
        {
        }
    }

    private void PollCore()
    {
        if (!Monitoring.IsRunning && Monitoring.SessionFilePath is null)
        {
            UpdateDiagnosticsAndStorageEvents();
            return;
        }

        var windows = Monitoring.GetWindows();
        var latest = Monitoring.GetLatest();
        foreach (var (instance, point) in latest)
        {
            var row = ResolveRow(instance);
            row.InstanceName = instance;
            _rowsByInstance[instance] = row;
            if (instance.StartsWith("Storage Space:", StringComparison.OrdinalIgnoreCase))
            {
                var zh = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
                row.ActivityText = point.VirtualDiskProblemBytes > 0
                    ? point.VirtualDiskRegeneratingBytes > 0
                        ? zh ? "修复中" : "Repairing"
                        : zh ? "警告" : "Warning"
                    : zh ? "正常" : "Healthy";
                row.ReadText =
                    $"{(zh ? "活动" : "Active")} {FormatBytes(point.VirtualDiskActiveBytes)}";
                row.WriteText = point.VirtualDiskProblemBytes > 0
                    ? $"{(zh ? "异常" : "Issue")} {FormatBytes(point.VirtualDiskProblemBytes)}"
                    : $"{(zh ? "异常" : "Issue")} 0 B";
            }
            else
            {
                row.ActivityText = $"{point.ActivityPercent:F0}%";
                row.ReadText = FormatRate(point.ReadBytesPerSecond);
                row.WriteText = FormatRate(point.WriteBytesPerSecond);
            }
        }

        var series = new List<DiskGraphSeries>();
        foreach (var row in _rows)
        {
            if (!row.ShowInGraph || row.InstanceName is null)
            {
                continue;
            }
            if (!windows.TryGetValue(row.InstanceName, out var points) || points.Length < 2)
            {
                continue;
            }
            series.Add(new DiskGraphSeries
            {
                InstanceName = row.InstanceName,
                DisplayName = row.Name,
                Color = row.SeriesColor,
                Points = points
            });
        }
        ActivityGraph.SetSeries(series);
        UpdateDiagnosticsAndStorageEvents();
        PublishNewStorageHealthEvent();
    }

    private void UpdateDiagnosticsAndStorageEvents()
    {
        var zh = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var diagnostics = Monitoring.GetDiagnostics();
        var runningState = Monitoring.IsRunning
            ? (zh ? "运行中" : "running")
            : (zh ? "未运行" : "stopped");
        if (!Monitoring.IsRunning && !string.IsNullOrWhiteSpace(Monitoring.LastError))
        {
            MonitorStatusText.Text = zh
                ? $"监控状态：{runningState}；原因：{Monitoring.LastError}"
                : $"Monitoring: {runningState}; reason: {Monitoring.LastError}";
        }
        else
        {
        var queue = diagnostics.SubscriberCapacity > 0
            ? $"{diagnostics.SubscriberBufferedSamples}/{diagnostics.SubscriberCapacity}"
            : "0/0";
        var dropDetails = zh
            ? $"窗口 {diagnostics.WindowDroppedSamples}、持久化 {diagnostics.PersistenceDroppedSamples}、订阅 {diagnostics.SubscriberDroppedSamples}、拒绝源 {diagnostics.RejectedSourceSamples}；订阅队列 {queue}（{diagnostics.ActiveSubscribers} 个）"
            : $"window {diagnostics.WindowDroppedSamples}, persistence {diagnostics.PersistenceDroppedSamples}, subscriber {diagnostics.SubscriberDroppedSamples}, rejected source {diagnostics.RejectedSourceSamples}; subscriber queue {queue} ({diagnostics.ActiveSubscribers})";
        MonitorStatusText.Text = diagnostics.ConsecutiveFailures > 0
            ? zh
                ? $"采样异常：连续失败 {diagnostics.ConsecutiveFailures} 次；代码 {diagnostics.LastFailureCode ?? "unknown"}；窗口样本 {diagnostics.WindowSampleCount}；Agent 丢样 {diagnostics.AgentDroppedSamples}（{dropDetails}）"
                : $"Sampling warning: {diagnostics.ConsecutiveFailures} consecutive failures; code {diagnostics.LastFailureCode ?? "unknown"}; {diagnostics.WindowSampleCount} window samples; {diagnostics.AgentDroppedSamples} Agent drops ({dropDetails})"
            : zh
                ? $"监控状态：{runningState}；采样正常；最近成功 {FormatTimestamp(diagnostics.LastSuccessfulSampleUtc)}；窗口样本 {diagnostics.WindowSampleCount}；Agent 丢样 {diagnostics.AgentDroppedSamples}（{dropDetails}）"
                : $"Monitoring: {runningState}; sampling healthy; last success {FormatTimestamp(diagnostics.LastSuccessfulSampleUtc)}; {diagnostics.WindowSampleCount} window samples; {diagnostics.AgentDroppedSamples} Agent drops ({dropDetails})";
        }

        var displayRows = Monitoring.GetRecentStorageHealthEvents()
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(20)
            .Select(item =>
                $"{item.OccurredAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {item.Severity} · {item.Provider} · Event {item.EventId} · Record {item.RecordId?.ToString() ?? "-"}")
            .ToArray();
        if (_storageEventRows.SequenceEqual(displayRows, StringComparer.Ordinal))
        {
            return;
        }

        _storageEventRows.Clear();
        foreach (var row in displayRows)
        {
            _storageEventRows.Add(row);
        }
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    private void PublishNewStorageHealthEvent()
    {
        var newest = Monitoring.GetRecentStorageHealthEvents()
            .Where(item => item.OccurredAtUtc > _storageEventCutoff)
            .OrderBy(item => item.OccurredAtUtc)
            .LastOrDefault();
        if (newest is null)
        {
            return;
        }

        _storageEventCutoff = newest.OccurredAtUtc;
        var zh = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var title = zh ? "存储健康事件" : "Storage health event";
        var summary =
            $"{newest.Provider} · Event {newest.EventId} · {newest.Severity}";
        if (newest.Severity is StorageHealthEventSeverity.Critical
            or StorageHealthEventSeverity.Error)
        {
            _viewModel.NotificationService.PublishError(
                title,
                summary,
                "monitor",
                $"storage-event:{newest.Channel}:{newest.RecordId}:{newest.EventId}");
        }
        else if (newest.Severity == StorageHealthEventSeverity.Warning)
        {
            _viewModel.NotificationService.PublishWarning(
                title,
                summary,
                "monitor",
                $"storage-event:{newest.Channel}:{newest.RecordId}:{newest.EventId}");
        }
    }

    private MonitorRowViewModel ResolveRow(string instance)
    {
        if (_rowsByInstance.TryGetValue(instance, out var row))
        {
            return row;
        }

        var number = MonitoringService.ParseDiskNumber(instance);
        if (number is not null && _rowsByDiskNumber.TryGetValue(number.Value, out row!))
        {
            return row;
        }

        row = AddRow(
            instance,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            number,
            instance.Contains(':') &&
            !instance.StartsWith("Storage Space:", StringComparison.OrdinalIgnoreCase));
        return row;
    }

    private void Guard(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogMonitorFailure(name, ex);
        }
    }

    private async void ContinuousMonitoringSwitch_Toggled(
        object sender,
        Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready || _updatingContinuousMonitoring)
        {
            return;
        }

        var enabled = ContinuousMonitoringSwitch.IsOn;
        _updatingContinuousMonitoring = true;
        try
        {
            await _viewModel.SetContinuousMonitoringAsync(enabled);
            if (!enabled)
            {
                await Monitoring.StopAsync();
                return;
            }

            if (!await Monitoring.StartAsync(SelectedRate()))
            {
                LogMonitorFailure(
                    "ContinuousMonitoringStart",
                    new InvalidOperationException(
                        $"LastError={Monitoring.LastError ?? "<null>"}; "
                            + $"IsRunning={Monitoring.IsRunning}; "
                            + $"SessionFilePath={Monitoring.SessionFilePath ?? "<null>"}"));
                await _viewModel.SetContinuousMonitoringAsync(false);
                ContinuousMonitoringSwitch.IsOn = false;
                _viewModel.NotificationService.PublishWarning(
                    _viewModel.Localization["MonitorIntro"],
                    Monitoring.LastError ?? "监控 Agent 未能启动。",
                    "monitor",
                    "monitor-start-failed");
            }
        }
        catch (Exception exception)
        {
            LogMonitorFailure("ContinuousMonitoringClick", exception);
        }
        finally
        {
            _updatingContinuousMonitoring = false;
        }
    }

    private async void RateOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ApplySelectedRateAsync();
    }

    private async void RateOptions_DropDownClosed(object sender, object e)
    {
        await ApplySelectedRateAsync();
    }

    private async Task ApplySelectedRateAsync()
    {
        if (!_ready || _applyingSampleRate)
        {
            return;
        }

        var rate = SelectedRate();
        if (Math.Abs(Monitoring.SampleRateHz - rate) < 0.001)
        {
            return;
        }

        _applyingSampleRate = true;
        try
        {
            await _viewModel.SetMonitoringSampleRateAsync(rate);
            await Monitoring.SetRateAsync(rate);
        }
        catch (Exception exception)
        {
            LogMonitorFailure("RateChanged", exception);
        }
        finally
        {
            _applyingSampleRate = false;
            ApplyPollInterval();
        }
    }

    private async void EventsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (MonitorRoot.XamlRoot is null)
        {
            return;
        }

        var events = Monitoring.GetRecentStorageHealthEvents()
            .OrderByDescending(item => item.OccurredAtUtc)
            .ToArray();
        var rows = new StackPanel { Spacing = 10 };
        if (events.Length == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = _viewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                    ? "当前没有可显示的监控事件。"
                    : "There are no monitoring events to display.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var item in events)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = $"{item.OccurredAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {item.Severity} · {item.Provider}\n{item.Message}",
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = MonitorRoot.XamlRoot,
            Title = _viewModel.Localization["MonitoringEvents"],
            CloseButtonText = _viewModel.Localization["Close"],
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = rows, MaxHeight = 360 }
        };
        await dialog.ShowAsync();
    }

    private async void ExportButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            await ExportSessionAsync();
        }
        catch (Exception ex)
        {
            LogMonitorFailure("ExportClick", ex);
        }
    }

    private async Task ExportSessionAsync()
    {
        var l = _viewModel.Localization;
        await Monitoring.FlushAsync();
        var sessionPath = Monitoring.SessionFilePath;
        if (!Monitoring.UsesAgent
            && (sessionPath is null || !File.Exists(sessionPath)))
        {
            _viewModel.NotificationService.PublishInfo(
                l["ExportData"],
                l["NoMonitoringData"],
                "monitor",
                $"monitor-export:{DateTimeOffset.UtcNow.Ticks}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"WinPool-Monitor-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            if (!await Monitoring.ExportCsvAsync(
                    file.Path,
                    overwrite: true))
            {
                _viewModel.NotificationService.PublishInfo(
                    l["ExportData"],
                    l["NoMonitoringData"],
                    "monitor",
                    $"monitor-export:{DateTimeOffset.UtcNow.Ticks}");
                return;
            }

            _viewModel.NotificationService.PublishInfo(
                l["ExportData"],
                l["Exported"],
                "monitor",
                $"monitor-export:{DateTimeOffset.UtcNow.Ticks}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.NotificationService.PublishError(
                l["Error"],
                $"{l["OperationFailed"]} {ex.Message}".Trim(),
                "monitor",
                $"monitor-export:{DateTimeOffset.UtcNow.Ticks}");
        }
    }

    private static string FormatRate(double bytesPerSecond) =>
        DiskActivityGraphControl.FormatRate(bytesPerSecond);

    private static string FormatBytes(double bytes) =>
        bytes >= 1024 * 1024 * 1024
            ? $"{bytes / (1024 * 1024 * 1024):F1} GiB"
            : bytes >= 1024 * 1024
                ? $"{bytes / (1024 * 1024):F1} MiB"
                : bytes >= 1024
                    ? $"{bytes / 1024:F1} KiB"
                    : $"{bytes:F0} B";

    private static string FormatCapacity(long bytes) => $"{bytes / 1073741824.0:F0} GiB";
}
