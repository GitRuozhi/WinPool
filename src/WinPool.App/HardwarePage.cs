using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Infrastructure.Windows;
using WinPool_App.Controls;

namespace WinPool_App;

/// <summary>Read-only hardware report. Source matching and value selection stay outside the UI.</summary>
public sealed partial class HardwarePage : Page
{
    private const double LabelMaxWidth = PropertyTableVisuals.LabelColumnMaxWidth;
    private const double ColumnGap = PropertyTableVisuals.ColumnGap;
    private const double RowHeight = PropertyTableVisuals.RowHeight;
    private const double MaxValueWidth = PropertyTableVisuals.ValueColumnMaxWidth;
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Visibility = Visibility.Collapsed
    };
    private readonly Button refresh = new();
    private readonly Button export = new();
    private readonly StackPanel report = new() { Spacing = 20, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer pageScroll = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly List<Border> groupCells = [];
    private readonly List<(Grid Labels, Grid Values, ScrollViewer Horizontal)> tables = [];
    private readonly Dictionary<Border, TableCellContext> tableCellContexts = [];
    private readonly Dictionary<string, TableGroupContext> tableGroupContexts = new(StringComparer.Ordinal);
    private WorkspaceViewModel viewModel = null!;
    private CancellationTokenSource? capture;
    private string? selectedGroupKey;
    private string? hoveredGroupKey;
    private bool active;

    public HardwarePage()
    {
        InitializeComponent();
        var buttonStyle = (Style)Application.Current.Resources["WinPoolButtonBaseStyle"];
        refresh.Style = buttonStyle;
        export.Style = buttonStyle;
        AutomationProperties.SetAutomationId(refresh, "HardwareRefresh");
        AutomationProperties.SetAutomationId(export, "HardwareExport");
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 8
        };
        actions.Children.Add(ContextHelp.Wrap(refresh));
        actions.Children.Add(ContextHelp.Wrap(export));
        var content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(actions);
        content.Children.Add(status);
        content.Children.Add(report);
        pageScroll.Content = content;
        Content = pageScroll;
        refresh.Click += Refresh_Click;
        export.Click += Export_Click;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        viewModel = (WorkspaceViewModel)e.Parameter;
        active = true;
        viewModel.PropertyChanged += Changed;
        viewModel.WorkspaceSelectionChanged += SelectionChanged;
        viewModel.Localization.PropertyChanged += Changed;
        ActualThemeChanged += HardwarePage_ActualThemeChanged;
        Rebuild();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        capture?.Cancel();
        viewModel.PropertyChanged -= Changed;
        viewModel.WorkspaceSelectionChanged -= SelectionChanged;
        viewModel.Localization.PropertyChanged -= Changed;
        ActualThemeChanged -= HardwarePage_ActualThemeChanged;
        base.OnNavigatedFrom(e);
    }

    private void HardwarePage_ActualThemeChanged(FrameworkElement sender, object args) => Rebuild();

    private void SelectionChanged(object? sender, EventArgs e) => Rebuild();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, viewModel.Localization) || e.PropertyName is nameof(WorkspaceViewModel.SelectedSystem)
            or nameof(WorkspaceViewModel.Snapshot) or nameof(WorkspaceViewModel.IsScanning)) Rebuild();
    }

    private void Rebuild()
    {
        SetButtonContent(refresh, capture is null ? "\uE72C" : "\uE711",
            capture is null ? viewModel.Localization["HardwareRefresh"] : viewModel.Localization["Cancel"]);
        SetButtonContent(export, "\uEDE1", viewModel.Localization["HardwareExport"]);
        refresh.IsEnabled = capture is not null || viewModel.SelectedSystem.IsLocal && !viewModel.IsScanning;
        export.IsEnabled = capture is null;
        var refreshDisabledReason = capture is not null
            ? Text("正在刷新；可选择“取消”。", "Refreshing; select Cancel to stop.")
            : !viewModel.SelectedSystem.IsLocal
                ? Text("硬件事实来自本机只读采集；模拟系统不能刷新硬件。",
                    "Hardware facts come from a local read-only scan; a simulated system cannot refresh them.")
                : viewModel.IsScanning
                    ? Text("正在扫描本机存储；扫描完成后可刷新硬件。",
                        "Local storage is being scanned; refresh becomes available when the scan completes.")
                    : string.Empty;
        ContextHelp.Set(refresh, capture is not null
            ? Text("取消当前只读硬件刷新。", "Cancel the current read-only hardware refresh.")
            : Text("只读刷新本机硬件信息。", "Refresh local hardware information without changing storage."));
        ContextHelp.Set(export, capture is null
            ? Text("导出当前本机或模拟系统的硬件报告。", "Export the hardware report for the current local or simulated system.")
            : Text("正在刷新时不能导出报告。", "Export is unavailable while a refresh is running."));
        ContextHelp.SetDisabledReason(refresh, refresh.IsEnabled ? null : refreshDisabledReason);
        ContextHelp.SetDisabledReason(export, export.IsEnabled
            ? null
            : Text("正在刷新时不能导出报告。", "Export is unavailable while a refresh is running."));
        SetStatus(null);
        report.Children.Clear();
        groupCells.Clear();
        tables.Clear();
        tableCellContexts.Clear();
        tableGroupContexts.Clear();
        hoveredGroupKey = null;
        var categories = HardwareReportProjector.Project(viewModel.SelectedSystem, viewModel.Localization.IsChinese);
        if (categories.Count == 0)
        {
            report.Children.Add(new TextBlock { Text = viewModel.Localization["HardwareEmpty"], TextWrapping = TextWrapping.Wrap });
            return;
        }
        for (var index = 0; index < categories.Count; index++)
        {
            report.Children.Add(BuildCategory(categories[index], index));
        }
        if (selectedGroupKey is not null && !groupCells.Any(cell => Equals(cell.Tag, selectedGroupKey)))
        {
            selectedGroupKey = null;
        }
        ApplyGroupHighlight();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            SyncLabelRowHeights);
    }

    private FrameworkElement BuildCategory(HardwareReportCategory category, int categoryIndex)
    {
        var panel = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = category.Name, FontSize = 18, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(heading);
        for (var sectionIndex = 0; sectionIndex < category.Sections.Count; sectionIndex++)
        {
            var section = category.Sections[sectionIndex];
            var table = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            table.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = LabelMaxWidth });
            table.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var labels = new Grid();
            var values = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var count = Math.Max(1, section.Rows.Select(x => x.Cells.Count).DefaultIfEmpty(1).Max());
            var keys = Enumerable.Range(0, count).Select(column =>
            {
                var objectId = section.Rows.Select(row => row.Cells.ElementAtOrDefault(column)?.ObjectId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                return $"{categoryIndex}:{sectionIndex}:{objectId ?? column.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }).ToArray();
            for (var column = 0; column < count; column++)
            {
                var rows = section.Rows.Select(row =>
                {
                    var cell = row.Cells.ElementAtOrDefault(column);
                    return new PropertyTableCopyRow(row.Label, cell?.Value ?? string.Empty);
                }).ToArray();
                var rawFields = section.Rows
                    .Select(row => row.Cells.ElementAtOrDefault(column)?.RawFields)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                tableGroupContexts[keys[column]] = new(
                    category.Name,
                    rows,
                    rawFields.Length == 0
                        ? viewModel.Localization["RawFieldsUnavailable"]
                        : string.Join(Environment.NewLine + Environment.NewLine, rawFields));
            }
            for (var column = 0; column < count; column++)
            {
                values.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            }
            for (var rowIndex = 0; rowIndex < section.Rows.Count; rowIndex++)
            {
                labels.RowDefinitions.Add(new() { Height = GridLength.Auto });
                values.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var row = section.Rows[rowIndex];
                var label = BorderCell(new TextBlock { Text = row.Label, Padding = new Thickness(10, 6, 10, 6),
                    VerticalAlignment = VerticalAlignment.Center, MaxWidth = LabelMaxWidth, Opacity = 0.72,
                    TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetRow(label, rowIndex);
                labels.Children.Add(label);
                for (var column = 0; column < count; column++)
                {
                    var cell = row.Cells.ElementAtOrDefault(column);
                    var text = new TextBlock
                    {
                        Text = cell?.Value ?? string.Empty,
                        Padding = new Thickness(10, 5, 10, 5),
                        MaxWidth = MaxValueWidth,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.WrapWholeWords
                    };
                    AutomationProperties.SetName(text, $"{category.Name}, {row.Label}, {text.Text}");
                    var border = PropertyTableVisuals.CreateCell(
                        text,
                        (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                        column == 0 ? 0 : ColumnGap,
                        tag: keys[column]);
                    border.Tapped += GroupCell_Tapped;
                    border.RightTapped += GroupCell_RightTapped;
                    border.PointerEntered += GroupCell_PointerEntered;
                    border.PointerExited += GroupCell_PointerExited;
                    Grid.SetRow(border, rowIndex);
                    Grid.SetColumn(border, column);
                    values.Children.Add(border);
                    groupCells.Add(border);
                    tableCellContexts[border] = new(keys[column], cell?.Value ?? string.Empty);
                }
            }
            Grid.SetColumn(labels, 0);
            table.Children.Add(labels);
            var horizontal = new ScrollViewer { Content = values, HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(horizontal, 1);
            table.Children.Add(horizontal);
            values.PointerWheelChanged += Values_PointerWheelChanged;
            tables.Add((labels, values, horizontal));
            panel.Children.Add(table);
        }
        return panel;
    }

    private static Border BorderCell(UIElement child) => PropertyTableVisuals.CreateCell(
        child,
        (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        minHeight: 34);

    private static void SetButtonContent(Button button, string glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        AutomationProperties.SetName(button, text);
    }

    private void SetStatus(string? text)
    {
        status.Text = text ?? string.Empty;
        status.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncLabelRowHeights()
    {
        foreach (var (labels, values, _) in tables)
        {
            for (var row = 0; row < labels.RowDefinitions.Count && row < values.RowDefinitions.Count; row++)
            {
                var height = values.Children.OfType<FrameworkElement>()
                    .Where(item => Grid.GetRow(item) == row)
                    .Select(item => item.ActualHeight)
                    .DefaultIfEmpty(RowHeight)
                    .Max();
                labels.RowDefinitions[row].Height = new GridLength(Math.Max(RowHeight, height));
            }
        }
    }

    private void Values_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        pageScroll.ChangeView(null, pageScroll.VerticalOffset - ((delta / 120.0) * 48), null, disableAnimation: true);
    }

    private void GroupCell_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        selectedGroupKey = ((FrameworkElement)sender).Tag as string;
        ApplyGroupHighlight();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            CenterSelectedGroup);
    }

    private void GroupCell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border cell
            || !tableCellContexts.TryGetValue(cell, out var context)
            || context.GroupKey != selectedGroupKey
            || !tableGroupContexts.TryGetValue(context.GroupKey, out var group))
        {
            return;
        }

        PropertyTableContextMenu.Show(
            cell,
            e.GetPosition(cell),
            XamlRoot,
            new PropertyTableContextMenuRequest(
                context.Value,
                group.Title,
                group.Rows,
                group.RawFields,
                viewModel.Localization["CopyCurrentValue"],
                viewModel.Localization["CopyGroup"],
                viewModel.Localization["RawFields"],
                viewModel.Localization["Close"]));
    }

    private void GroupCell_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        hoveredGroupKey = ((FrameworkElement)sender).Tag as string;
        ApplyGroupHighlight();
    }

    private void GroupCell_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        hoveredGroupKey = null;
        ApplyGroupHighlight();
    }

    private void ApplyGroupHighlight()
    {
        var accent = (Brush)Application.Current.Resources["WinPoolAccentBrush"];
        var accentForeground = (Brush)Application.Current.Resources["WinPoolAccentForegroundBrush"];
        var hover = (Brush)Application.Current.Resources["WinPoolAccentHoverBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var cell in groupCells)
        {
            var key = cell.Tag as string;
            var selected = key is not null && key == selectedGroupKey;
            cell.Background = selected ? accent : key is not null && key == hoveredGroupKey ? hover : transparent;
            if (cell.Child is not TextBlock text) continue;
            if (selected) text.Foreground = accentForeground;
            else text.ClearValue(TextBlock.ForegroundProperty);
            text.IsTextSelectionEnabled = false;
        }
    }

    private void CenterSelectedGroup()
    {
        if (selectedGroupKey is null) return;
        foreach (var (_, values, horizontal) in tables)
        {
            var cell = values.Children.OfType<Border>().FirstOrDefault(item =>
                Grid.GetRow(item) == 0 && Equals(item.Tag, selectedGroupKey));
            if (cell is null) continue;
            var bounds = cell.TransformToVisual(values).TransformBounds(
                new Windows.Foundation.Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
            var target = bounds.X - ((horizontal.ViewportWidth - bounds.Width) / 2);
            horizontal.ChangeView(Math.Max(0, target), null, null, disableAnimation: false);
            return;
        }
    }

    private sealed record TableCellContext(string GroupKey, string Value);

    private sealed record TableGroupContext(
        string Title,
        IReadOnlyList<PropertyTableCopyRow> Rows,
        string RawFields);

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (capture is not null) { capture.Cancel(); return; }
        if (!viewModel.SelectedSystem.IsLocal) return;
        capture = new CancellationTokenSource(); Rebuild();
        try
        {
            await viewModel.RefreshHardwareAsync(capture.Token);
            PublishHardwareInfo(
                Text("硬件信息已刷新", "Hardware information refreshed"),
                Text("已完成本机只读硬件刷新。", "The local read-only hardware refresh completed."),
                "hardware.refresh.completed");
        }
        catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            PublishHardwareFailure(
                Text("硬件刷新失败", "Hardware refresh failed"),
                exception,
                "hardware.refresh.exception");
        }
        finally { capture.Dispose(); capture = null; if (active) Rebuild(); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        export.IsEnabled = false;
        ContextHelp.SetDisabledReason(export,
            Text("正在导出硬件报告；完成后可以再次导出。", "The hardware report is exporting; export is available again when it finishes."));
        SetStatus(null);
        try
        {
            var targetId = viewModel.SelectedSystem.Id;
            var path = await viewModel.ExportActiveSystemAsync();
            if (path is not null && active && viewModel.SelectedSystem.Id == targetId)
            {
                SetStatus(viewModel.Localization["Exported"]);
                PublishHardwareInfo(
                    Text("硬件报告已导出", "Hardware report exported"),
                    viewModel.Localization["Exported"],
                    "hardware.export.completed");
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            PublishHardwareFailure(
                Text("硬件导出失败", "Hardware export failed"),
                exception,
                "hardware.export.exception");
        }
        finally
        {
            if (active)
            {
                export.IsEnabled = true;
                ContextHelp.SetDisabledReason(export, null);
            }
        }
    }

    private string Text(string zh, string en) =>
        viewModel.Localization.IsChinese ? zh : en;

    private void PublishHardwareFailure(string title, Exception exception, string code)
    {
        var target = viewModel.SelectedSystem.IsLocal
            ? Text($"本机目标：{viewModel.SelectedSystem.DisplayName}",
                $"Local target: {viewModel.SelectedSystem.DisplayName}")
            : Text($"模拟目标：{viewModel.SelectedSystem.DisplayName}",
                $"Simulated target: {viewModel.SelectedSystem.DisplayName}");
        viewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Error,
            title,
            $"{target}{Environment.NewLine}{Text("操作未完成。请检查连接、权限或当前选择后重试。", "The operation did not complete. Check the connection, permissions, or current selection, then try again.")}",
            "hardware",
            new GlobalNotificationOptions
            {
                OccurrenceKey = code,
                Code = code,
                SystemId = viewModel.SelectedSystem.Id,
                Target = target,
                Detail = $"{exception.GetType().Name}: {exception.Message}"
            });
    }

    private void PublishHardwareInfo(string title, string message, string code)
    {
        var target = viewModel.SelectedSystem.IsLocal
            ? Text($"本机目标：{viewModel.SelectedSystem.DisplayName}",
                $"Local target: {viewModel.SelectedSystem.DisplayName}")
            : Text($"模拟目标：{viewModel.SelectedSystem.DisplayName}",
                $"Simulated target: {viewModel.SelectedSystem.DisplayName}");
        viewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Info,
            title,
            $"{target}{Environment.NewLine}{message}",
            "hardware",
            new GlobalNotificationOptions
            {
                OccurrenceKey = code,
                Code = code,
                SystemId = viewModel.SelectedSystem.Id,
                Target = target,
                Detail = message
            });
    }
}
