using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Core;

namespace WinPool_App;

public sealed partial class MainPage : Page
{
    private const double LabelColumnWidth = 150;
    private const double ObjectColumnWidth = 220;
    private const double ColumnGap = 8;
    private readonly Dictionary<string, int> _columnIndexByKey = new(StringComparer.Ordinal);
    private readonly List<FrameworkElement> _columnCells = [];

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        ViewModel.WorkspaceSelectionChanged += ViewModel_WorkspaceSelectionChanged;
        Bindings.Update();
        RebuildComparisonTable();
        BuildCommandButtons();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.WorkspaceSelectionChanged -= ViewModel_WorkspaceSelectionChanged;
        ViewModel.TopologyHorizontalOffset = TopologyScrollViewer.HorizontalOffset;
        ViewModel.TopologyVerticalOffset = TopologyScrollViewer.VerticalOffset;
        base.OnNavigatedFrom(e);
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Snapshot.ScannedAt == DateTimeOffset.MinValue && !ViewModel.IsScanning)
        {
            await ViewModel.ScanAsync();
        }
        DispatcherQueue.TryEnqueue(() =>
            TopologyScrollViewer.ChangeView(
                ViewModel.TopologyHorizontalOffset,
                ViewModel.TopologyVerticalOffset,
                null,
                disableAnimation: true));
        RebuildComparisonTable();
        BuildCommandButtons();
    }

    private void RebuildComparisonTable()
    {
        var grid = ComparisonTableGrid;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        _columnIndexByKey.Clear();
        _columnCells.Clear();

        var columns = ViewModel.ComparisonColumns;
        if (columns.Count == 0)
        {
            grid.Children.Add(new TextBlock
            {
                Padding = new Thickness(12),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Text = ViewModel.Localization["NoSelection"]
            });
            return;
        }

        var labels = new List<string>();
        foreach (var column in columns)
        {
            foreach (var row in column.Rows)
            {
                if (!labels.Contains(row.Label, StringComparer.Ordinal))
                {
                    labels.Add(row.Label);
                }
            }
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        for (var i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ObjectColumnWidth) });
            _columnIndexByKey[columns[i].Key] = i;
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var unused in labels)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var corner = new Border { Padding = new Thickness(10, 8, 10, 8) };
        Grid.SetRow(corner, 0);
        Grid.SetColumn(corner, 0);
        grid.Children.Add(corner);

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var header = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(i == 0 ? 0 : ColumnGap, 0, 0, 4),
                Content = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    Text = column.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                Tag = column.Key
            };
            header.SetValue(AutomationProperties.NameProperty, column.Name);
            header.Click += ColumnHeader_Click;
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, i + 1);
            grid.Children.Add(header);
            _columnCells.Add(header);
        }

        for (var rowIndex = 0; rowIndex < labels.Count; rowIndex++)
        {
            var labelBlock = new TextBlock
            {
                Padding = new Thickness(10, 7, 10, 7),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Text = labels[rowIndex]
            };
            Grid.SetRow(labelBlock, rowIndex + 1);
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            for (var i = 0; i < columns.Count; i++)
            {
                var value = columns[i].Rows
                    .FirstOrDefault(x => x.Label.Equals(labels[rowIndex], StringComparison.Ordinal))
                    ?.Value ?? string.Empty;
                var cell = new Border
                {
                    Margin = new Thickness(i == 0 ? 0 : ColumnGap, 0, 0, 0),
                    BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new TextBlock
                    {
                        Padding = new Thickness(10, 7, 10, 7),
                        Text = value,
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    Tag = columns[i].Key
                };
                cell.Tapped += ColumnCell_Tapped;
                Grid.SetRow(cell, rowIndex + 1);
                Grid.SetColumn(cell, i + 1);
                grid.Children.Add(cell);
                _columnCells.Add(cell);
            }
        }

        ApplyColumnHighlight(centerSelected: false);
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e) =>
        SelectColumn(((FrameworkElement)sender).Tag as string);

    private void ColumnCell_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        SelectColumn(((FrameworkElement)sender).Tag as string);

    private void SelectColumn(string? key)
    {
        if (key is null)
        {
            return;
        }
        var item = ViewModel.Objects.FirstOrDefault(x => x.Key == key);
        if (item is not null && !ReferenceEquals(item, ViewModel.SelectedWorkspaceItem))
        {
            ViewModel.SelectedWorkspaceItem = item;
        }
        else
        {
            ApplyColumnHighlight(centerSelected: true);
        }
    }

    private void ApplyColumnHighlight(bool centerSelected)
    {
        var accent = (Brush)Application.Current.Resources["WinPoolAccentHoverBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var selectedKey = ViewModel.SelectedWorkspaceItem?.Key;
        foreach (var cell in _columnCells)
        {
            var brush = cell.Tag is string key && key == selectedKey ? accent : transparent;
            switch (cell)
            {
                case Border border:
                    border.Background = brush;
                    break;
                case Button button:
                    button.Background = brush;
                    break;
            }
        }

        if (centerSelected
            && selectedKey is not null
            && _columnIndexByKey.TryGetValue(selectedKey, out var index))
        {
            var columnStart = LabelColumnWidth + (index * (ObjectColumnWidth + ColumnGap));
            var target = columnStart - ((TableScrollViewer.ViewportWidth - ObjectColumnWidth) / 2);
            TableScrollViewer.ChangeView(Math.Max(0, target), null, null, disableAnimation: false);
        }
    }

    private void BuildCommandButtons()
    {
        if (ViewModel is null)
        {
            return;
        }
        CommandButtonsPanel.Children.Clear();
        var selected = ViewModel.SelectedWorkspaceItem;
        if (selected?.IsAction == true)
        {
            AddCommand(Text("导入", "Import"), "\uE8B5", true, ImportAsync);
            return;
        }
        if (selected?.Unit is null)
        {
            return;
        }

        var simulated = ViewModel.IsUsingSimulatedInventory;
        switch (ViewModel.SelectedCategory)
        {
            case WorkspaceCategory.System:
                AddCommand(Text("刷新", "Refresh"), "\uE72C", ViewModel.IsLocalSystem, RescanAsync);
                AddCommand(Text("导出", "Export"), "\uEDE1", true, ExportAsync);
                break;
            case WorkspaceCategory.Pool:
                var pool = ViewModel.ActiveSnapshot.StoragePools.FirstOrDefault(
                    x => x.StableId == selected.Unit.StableId);
                var editablePool = simulated && pool is { IsPrimordial: false };
                AddCommand(Text("重命名", "Rename"), "\uE8AC", editablePool, NavigateEditAsync);
                AddCommand(Text("创建", "Create"), "\uE710", simulated && pool is not null, NavigateEditAsync);
                AddCommand(Text("调整", "Adjust"), "\uE90F", editablePool, NavigateEditAsync);
                AddCommand(
                    Text("优化使用率", "Optimize usage"), "\uE945",
                    editablePool,
                    NavigateEditAsync);
                break;
            case WorkspaceCategory.Tier:
                AddCommand(Text("重命名", "Rename"), "\uE8AC", simulated, NavigateEditAsync);
                AddCommand(Text("创建", "Create"), "\uE710", simulated, NavigateEditAsync);
                AddCommand(Text("调整", "Adjust"), "\uE90F", simulated, NavigateEditAsync);
                break;
            case WorkspaceCategory.Disk:
                var selectedDisk = ResolveOsDisk();
                var selectedPhysicalDisk = selectedDisk is null
                    ? null
                    : ViewModel.ActiveSnapshot.PhysicalDisks.FirstOrDefault(
                        x => x.StableId == selectedDisk.PhysicalDiskStableId);
                var diskCanBeTakenOffline = selectedDisk is { IsOffline: true }
                    || selectedDisk is
                    {
                        IsBoot: false,
                        IsSystem: false
                    } && selectedPhysicalDisk is not { IsPageFile: true } and not { IsCrashDump: true };
                var diskHasPartitions = selectedDisk is not null
                    && ViewModel.ActiveSnapshot.Partitions.Any(x => x.OsDiskStableId == selectedDisk.StableId);
                AddCommand(
                    Text("重命名", "Rename"), "\uE8AC",
                    simulated && selected.Unit.Kind is not StorageUnitKind.NetworkDisk,
                    NavigateEditAsync);
                AddCommand(Text("新建", "New"), "\uE710", simulated && selectedDisk is not null, NavigateEditAsync);
                AddCommand(Text("初始化", "Initialize"), "\uE9CE", simulated && selectedDisk is not null, NavigateEditAsync);
                AddCommand(
                    Text("转换", "Convert"), "\uE8AB",
                    simulated && selectedDisk is not null && !diskHasPartitions,
                    NavigateEditAsync);
                AddCommand(
                    Text("脱机 / 联机", "Offline / Online"), "",
                    simulated && selectedDisk is not null && diskCanBeTakenOffline,
                    NavigateEditAsync);
                AddCommand(Text("属性", "Properties"), "\uE90A", true, PropertiesAsync);
                break;
            case WorkspaceCategory.Partition:
                var partition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(
                    x => x.StableId == selected.Unit.StableId);
                var isPrimaryPartition = partition?.Type == "Primary";
                var editablePartition = simulated && isPrimaryPartition;
                AddCommand(
                    Text("打开", "Open"), "\uE838",
                    isPrimaryPartition && ViewModel.CanOpenSelectedPartition,
                    OpenPartitionAsync);
                AddCommand(Text("更改盘符", "Change drive letter"), "\uE8B7", editablePartition, NavigateEditAsync);
                AddCommand(Text("重命名", "Rename"), "\uE8AC", editablePartition, NavigateEditAsync);
                AddCommand(Text("格式化", "Format"), "\uE9CE", editablePartition, NavigateEditAsync);
                AddOptimizeCommand(partition, simulated);
                AddCommand(Text("调整", "Adjust"), "\uE90F", editablePartition, NavigateEditAsync);
                AddCommand(Text("删除", "Delete"), "\uE74D", editablePartition, NavigateEditAsync);
                AddCommand(Text("属性", "Properties"), "\uE90A", true, PropertiesAsync);
                break;
        }
    }

    private void AddOptimizeCommand(PartitionInfo? partition, bool simulated)
    {
        if (partition is null)
        {
            return;
        }

        var osDisk = ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(
            x => x.StableId == partition.OsDiskStableId);
        string label;
        if (osDisk?.VirtualDiskStableId is not null)
        {
            label = Text("优化", "Optimize");
        }
        else
        {
            var physical = ViewModel.ActiveSnapshot.PhysicalDisks.FirstOrDefault(
                x => x.StableId == osDisk?.PhysicalDiskStableId);
            label = physical?.MediaType == "HDD"
                ? Text("碎片整理", "Defragment")
                : Text("剪裁", "Trim");
        }
        AddCommand(label, "\uE945", simulated && partition.Type == "Primary", OptimizeDriveAsync);
    }

    private void AddCommand(string text, string glyph, bool enabled, Func<Task> action)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { FontSize = 14, Glyph = glyph },
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = text }
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = enabled
        };
        button.SetValue(AutomationProperties.NameProperty, text);
        if (!enabled && ViewModel.IsLocalSystem)
        {
            ToolTipService.SetToolTip(
                button,
                Text("本机存储当前为只读。", "Local storage is currently read-only."));
        }
        button.Click += async (_, _) => await RunCommandAsync(action);
        CommandButtonsPanel.Children.Add(button);
    }

    private async Task RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ViewModel.NotificationService.PublishError(
                ViewModel.Localization["Error"],
                $"{ViewModel.Localization["OperationFailed"]} {ex.Message}".Trim(),
                "workspace-operation",
                $"workspace-operation:{DateTimeOffset.UtcNow.Ticks}");
        }
        BuildCommandButtons();
    }

    private async Task RescanAsync() => await ViewModel.ScanAsync();

    private async Task ExportAsync()
    {
        if (await ViewModel.ExportActiveSystemAsync() is not null)
        {
            ViewModel.StatusMessage = ViewModel.Localization["Exported"];
        }
    }

    private async Task ImportAsync()
    {
        if (await ViewModel.ImportSystemAsync())
        {
            ViewModel.StatusMessage = Text("系统已导入为模拟副本。", "System imported as a simulation.");
        }
    }

    private Task NavigateEditAsync()
    {
        ((MainWindow)App.Window).ShowEdit(ViewModel.ResolveDetailUnit()?.StableId);
        return Task.CompletedTask;
    }

    private async Task OptimizeDriveAsync()
    {
        var unit = ViewModel.ResolveDetailUnit();
        if (unit is null)
        {
            return;
        }
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.OptimizeDrive,
            unit.StableId));
    }

    private OsDiskInfo? ResolveOsDisk()
    {
        var unit = ViewModel.ResolveDetailUnit();
        return unit?.Kind switch
        {
            StorageUnitKind.OsDisk =>
                ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(x => x.StableId == unit.StableId),
            StorageUnitKind.PhysicalDisk =>
                ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(x => x.PhysicalDiskStableId == unit.StableId),
            StorageUnitKind.VirtualDisk =>
                ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(x => x.VirtualDiskStableId == unit.StableId),
            _ => null
        };
    }

    private async Task OpenPartitionAsync()
    {
        var unit = ViewModel.ResolveDetailUnit();
        var partition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(
            x => x.StableId == unit?.StableId);
        if (!ViewModel.IsLocalSystem || partition is null || !Directory.Exists(partition.Path))
        {
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{partition.Path}\"",
            UseShellExecute = true
        });
        await Task.CompletedTask;
    }

    private async Task PropertiesAsync()
    {
        var zh = ViewModel.Localization.Language == LanguagePreference.ZhCn;
        var osDisk = ResolveOsDisk();
        var text = osDisk is not null
            ? WinPool.App.Services.DiskDetailFormatter.Format(ViewModel.ActiveSnapshot, osDisk, zh)
            : ViewModel.CreateSelectedSummary();
        await ShowMessageAsync(Text("属性", "Properties"), text);
    }

    private async Task ApplyAsync(SimulationOperationRequest request)
    {
        var result = await ViewModel.ApplySimulationOperationAsync(request);
        if (!result.Succeeded)
        {
            await ShowMessageAsync(Text("操作不可用", "Operation unavailable"), result.Error);
        }
    }

    private async Task<string?> PromptAsync(string title, string value)
    {
        var input = new TextBox { Text = value, MinWidth = 360 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? input.Text.Trim()
            : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
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
        await dialog.ShowAsync();
    }

    private string Text(string zh, string en) =>
        ViewModel.Localization.Language == LanguagePreference.ZhCn ? zh : en;

    private void TopologyScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(320, e.NewSize.Width - 20);
        TopologySystemsControl.Width = width;
        ViewModel.UpdateTopologyViewportWidth(width);
    }

    private void ViewModel_WorkspaceSelectionChanged(object? sender, EventArgs e)
    {
        RebuildComparisonTable();
        ApplyColumnHighlight(centerSelected: true);
        BuildCommandButtons();
    }
}
