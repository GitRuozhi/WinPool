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
using WinPool.Core;

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
    private readonly Dictionary<string, MonitorRowViewModel> _rowsByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, MonitorRowViewModel> _rowsByDiskNumber = new();
    private WorkspaceViewModel _viewModel = null!;
    private DispatcherQueueTimer? _pollTimer;
    private bool _ready;

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
        if (_viewModel is not null && !Monitoring.BackgroundEnabled && Monitoring.IsRunning)
        {
            _ = Monitoring.StopAsync();
        }
    }

    private MonitoringService Monitoring => _viewModel.Monitoring;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _viewModel = (WorkspaceViewModel)e.Parameter;
        MonitorRoot.Loaded += MonitorRoot_Loaded;
        Guard("UpdateText", UpdateText);
        Guard("PopulateRateOptions", PopulateRateOptions);
        Guard("BuildRows", BuildRows);
        BackgroundCheckBox.IsChecked = Monitoring.BackgroundEnabled;
        if (!Monitoring.IsRunning)
        {
            Guard("StartClick", () => Monitoring.Start(SelectedRate()));
        }
        UpdateRunningState();
        _pollTimer = DispatcherQueue.CreateTimer();
        ApplyPollInterval();
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
        Poll();
        _ready = true;
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
        if (!Monitoring.BackgroundEnabled)
        {
            _ = Monitoring.StopAsync();
        }
        base.OnNavigatedFrom(e);
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
        BackgroundCheckBox.Content = l["BackgroundMonitoring"];
        SamplingRateLabel.Text = l["SamplingRate"];
        ((TextBlock)((StackPanel)AutoColorsButton.Content).Children[1]).Text = l["AutoColor"];
        ((TextBlock)((StackPanel)StartButton.Content).Children[1]).Text = l["StartMonitoring"];
        ((TextBlock)((StackPanel)StopButton.Content).Children[1]).Text = l["StopMonitoring"];
        ((TextBlock)((StackPanel)ExportButton.Content).Children[1]).Text = l["ExportData"];
        ToolTipService.SetToolTip(RateOptions, l["RefreshRate"]);
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
            GraphRow.Height = new Microsoft.UI.Xaml.GridLength(available - table, Microsoft.UI.Xaml.GridUnitType.Star);
            TableRow.Height = new Microsoft.UI.Xaml.GridLength(table, Microsoft.UI.Xaml.GridUnitType.Star);
            _splitApplied = true;
        });
    }

    private void PopulateRateOptions()
    {
        RateOptions.ItemsSource = MonitoringService.RateOptions
            .Select(x => $"{x:0.#} Hz")
            .ToArray();
        var index = Array.IndexOf(MonitoringService.RateOptions, Monitoring.SampleRateHz);
        RateOptions.SelectedIndex = index >= 0 ? index : 2;
    }

    private double SelectedRate() =>
        RateOptions.SelectedIndex >= 0 && RateOptions.SelectedIndex < MonitoringService.RateOptions.Length
            ? MonitoringService.RateOptions[RateOptions.SelectedIndex]
            : 1;

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
            var directory = WinPool.Infrastructure.Windows.StorageDataLocations.CurrentRoot;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "monitor-debug.log"),
                $"{DateTime.Now:O} [{source}] {ex}\n\n");
        }
        catch
        {
        }
    }

    private void PollCore()
    {
        if (!Monitoring.IsRunning && Monitoring.SessionFilePath is null)
        {
            return;
        }

        var windows = Monitoring.GetWindows();
        var latest = Monitoring.GetLatest();
        foreach (var (instance, point) in latest)
        {
            var row = ResolveRow(instance);
            row.InstanceName = instance;
            _rowsByInstance[instance] = row;
            row.ActivityText = $"{point.ActivityPercent:F0}%";
            row.ReadText = FormatRate(point.ReadBytesPerSecond);
            row.WriteText = FormatRate(point.WriteBytesPerSecond);
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
            instance.Contains(':'));
        return row;
    }

    private void UpdateRunningState()
    {
        StartButton.IsEnabled = !Monitoring.IsRunning;
        StopButton.IsEnabled = Monitoring.IsRunning;
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

    private void BackgroundCheckBox_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }
        Guard("BackgroundClick", () => Monitoring.BackgroundEnabled = BackgroundCheckBox.IsChecked == true);
    }

    private void RateOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }
        Guard("RateChanged", () => { Monitoring.SetRate(SelectedRate()); });
        ApplyPollInterval();
    }

    private void StartButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Guard("StartClick", () => Monitoring.Start(SelectedRate()));
        UpdateRunningState();
    }

    private async void StopButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            await Monitoring.StopAsync();
            UpdateRunningState();
        }
        catch (Exception ex)
        {
            LogMonitorFailure("StopClick", ex);
        }
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
        if (sessionPath is null || !File.Exists(sessionPath))
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
            File.Copy(sessionPath, file.Path, true);
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

    private static string FormatCapacity(long bytes) => $"{bytes / 1073741824.0:F0} GiB";
}
