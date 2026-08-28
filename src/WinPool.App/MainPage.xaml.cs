using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

public sealed partial class MainPage : Page
{
    private const uint SeeMaskInvokeIdList = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Hwnd;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public nint InstApp;
        public nint IDList;
        public string? Class;
        public nint HKeyClass;
        public uint HotKey;
        public nint Icon;
        public nint Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);

    private static bool TryShowNativeProperties(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskInvokeIdList,
            Hwnd = nint.Zero,
            Verb = "properties",
            File = path,
            Show = 1
        };
        return ShellExecuteEx(ref info);
    }

    private const double LabelColumnWidth = 96;
    private const double ColumnGap = 8;
    private const double RowHeight = 32;
    private const double MaxValueWidth = 250;
    private readonly Dictionary<string, int> _columnIndexByKey = new(StringComparer.Ordinal);
    private readonly List<Border> _columnCells = [];
    private string? _hoveredColumnKey;
    private string _renderedSignature = string.Empty;

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
        ViewModel.NodeContextMenuRequested = ShowNodeContextMenu;
        ActualThemeChanged += MainPage_ActualThemeChanged;
        Bindings.Update();
        RebuildComparisonTable();
        BuildCommandButtons();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.WorkspaceSelectionChanged -= ViewModel_WorkspaceSelectionChanged;
        ViewModel.NodeContextMenuRequested = null;
        ActualThemeChanged -= MainPage_ActualThemeChanged;
        ViewModel.TopologyHorizontalOffset = TopologyScrollViewer.HorizontalOffset;
        ViewModel.TopologyVerticalOffset = TopologyScrollViewer.VerticalOffset;
        base.OnNavigatedFrom(e);
    }

    private void MainPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        _renderedSignature = string.Empty;
        RebuildComparisonTable();
        BuildCommandButtons();
        ApplyColumnHighlight(centerSelected: false);
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.AutoScanAttempted && !ViewModel.IsScanning)
        {
            ViewModel.AutoScanAttempted = true;
            _ = RefreshLocalInventoryAsync();
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

    private async Task RefreshLocalInventoryAsync()
    {
        await ViewModel.ScanAsync();
        DispatcherQueue.TryEnqueue(() =>
        {
            RebuildComparisonTable();
            BuildCommandButtons();
        });
    }

    private void RebuildComparisonTable()
    {
        var grid = ComparisonTableGrid;
        var labelGrid = LabelColumnGrid;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        labelGrid.Children.Clear();
        labelGrid.RowDefinitions.Clear();
        _columnIndexByKey.Clear();
        _columnCells.Clear();
        _hoveredColumnKey = null;

        var columns = ViewModel.ComparisonColumns;
        if (columns.Count == 0)
        {
            grid.Children.Add(new TextBlock
            {
                Padding = new Thickness(12),
                Foreground = ProbeSecondaryText.Foreground,
                Text = ViewModel.Localization["NoSelection"]
            });
            return;
        }

        var labels = new List<string> { ViewModel.Localization["Name"] };
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

        for (var i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _columnIndexByKey[columns[i].Key] = i;
        }
        foreach (var unused in labels)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            labelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var secondaryBrush = ProbeSecondaryText.Foreground;
        var dividerBrush = ProbeDivider.BorderBrush;

        for (var rowIndex = 0; rowIndex < labels.Count; rowIndex++)
        {
            var labelCell = new Border
            {
                MinHeight = RowHeight,
                BorderBrush = dividerBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new TextBlock
                {
                    Padding = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = secondaryBrush,
                    IsTextSelectionEnabled = true,
                    Text = labels[rowIndex],
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            Grid.SetRow(labelCell, rowIndex);
            labelGrid.Children.Add(labelCell);

            for (var i = 0; i < columns.Count; i++)
            {
                var isNameRow = rowIndex == 0;
                var value = isNameRow
                    ? columns[i].Name
                    : columns[i].Rows
                        .FirstOrDefault(x => x.Label.Equals(labels[rowIndex], StringComparison.Ordinal))
                        ?.Value ?? string.Empty;
                var text = new TextBlock
                {
                    Padding = new Thickness(10, 5, 10, 5),
                    MaxWidth = MaxValueWidth,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isNameRow ? FontWeights.SemiBold : FontWeights.Normal,
                    Text = value,
                    TextWrapping = TextWrapping.WrapWholeWords
                };
                var cell = new Border
                {
                    MinHeight = RowHeight,
                    Margin = new Thickness(i == 0 ? 0 : ColumnGap, 0, 0, 0),
                    BorderBrush = dividerBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = text,
                    Tag = columns[i].Key
                };
                cell.Tapped += ColumnCell_Tapped;
                cell.PointerEntered += ColumnCell_PointerEntered;
                cell.PointerExited += ColumnCell_PointerExited;
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
                _columnCells.Add(cell);
            }
        }

        ApplyColumnHighlight(centerSelected: false);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            SyncLabelRowHeights);
        _renderedSignature = ComputeTableSignature();
    }

    private string ComputeTableSignature()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(ViewModel.SelectedCategory).Append('|');
        foreach (var column in ViewModel.ComparisonColumns)
        {
            builder.Append(column.Key).Append('\u0001').Append(column.Name);
            foreach (var row in column.Rows)
            {
                builder.Append(row.Label).Append('\u0001').Append(row.Value).Append('\u0002');
            }
        }
        return builder.ToString();
    }

    private void SyncLabelRowHeights()
    {
        var grid = ComparisonTableGrid;
        var labelGrid = LabelColumnGrid;
        for (var row = 0; row < grid.RowDefinitions.Count && row < labelGrid.RowDefinitions.Count; row++)
        {
            var height = grid.Children
                .OfType<FrameworkElement>()
                .Where(x => Grid.GetRow(x) == row)
                .Select(x => x.ActualHeight)
                .DefaultIfEmpty(RowHeight)
                .Max();
            var target = Math.Max(RowHeight, height);
            var current = labelGrid.RowDefinitions[row].Height;
            if (current.IsAuto || Math.Abs(current.Value - target) > 0.5)
            {
                labelGrid.RowDefinitions[row].Height = new GridLength(target);
            }
        }
    }

    private void ComparisonTableGrid_PointerWheelChanged(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ComparisonTableGrid).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }
        e.Handled = true;
        var offset = TableOuterScrollViewer.VerticalOffset - ((delta / 120.0) * 48);
        TableOuterScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
    }

    private void ColumnCell_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        SelectColumn(((FrameworkElement)sender).Tag as string);

    private void ColumnCell_PointerEntered(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var key = ((Border)sender).Tag as string;
        if (key != _hoveredColumnKey)
        {
            _hoveredColumnKey = key;
            ApplyColumnHighlight(centerSelected: false);
        }
    }

    private void ColumnCell_PointerExited(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_hoveredColumnKey is not null)
        {
            _hoveredColumnKey = null;
            ApplyColumnHighlight(centerSelected: false);
        }
    }

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
        var accent = (Brush)Application.Current.Resources["WinPoolAccentBrush"];
        var accentForeground = (Brush)Application.Current.Resources["WinPoolAccentForegroundBrush"];
        var hover = (Brush)Application.Current.Resources["WinPoolAccentHoverBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var selectedKey = ViewModel.SelectedWorkspaceItem?.Key;
        foreach (var cell in _columnCells)
        {
            var isSelected = cell.Tag is string key && key == selectedKey;
            cell.Background = isSelected
                ? accent
                : cell.Tag is string hoveredKey && hoveredKey == _hoveredColumnKey
                    ? hover
                    : transparent;
            if (cell.Child is TextBlock text)
            {
                if (isSelected)
                {
                    text.Foreground = accentForeground;
                }
                else
                {
                    text.ClearValue(TextBlock.ForegroundProperty);
                }
                text.IsTextSelectionEnabled = isSelected;
            }
        }

        if (centerSelected
            && selectedKey is not null
            && _columnIndexByKey.TryGetValue(selectedKey, out var index))
        {
            var columnStart = _columnCells
                .Where(x => Grid.GetRow(x) == 0 && Grid.GetColumn(x) < index)
                .Sum(x => x.ActualWidth + (Grid.GetColumn(x) == 0 ? 0 : ColumnGap));
            var columnWidth = _columnCells
                .FirstOrDefault(x => Grid.GetRow(x) == 0 && Grid.GetColumn(x) == index)
                ?.ActualWidth ?? 220;
            var target = columnStart - ((TableScrollViewer.ViewportWidth - columnWidth) / 2);
            TableScrollViewer.ChangeView(Math.Max(0, target), null, null, disableAnimation: false);
        }
    }

    private sealed record CommandSpec(string Text, string Glyph, bool Enabled, Func<Task> Action);

    private List<CommandSpec> BuildCommandSpecs()
    {
        var selected = ViewModel.SelectedWorkspaceItem;
        if (selected?.IsAction == true)
        {
            return [new CommandSpec(Text("导入", "Import"), "\uE8B5", true, ImportAsync)];
        }

        var surface = ViewModel.GetSelectedCommandSurface();
        return surface is null
            ? []
            : surface.Commands.Select(BuildCommandSpec).ToList();
    }

    private CommandSpec BuildCommandSpec(ManageCommandView command) =>
        command.Kind switch
        {
            ManageCommandKind.RefreshLocal =>
                Spec("刷新本机信息", "Refresh local info", "\uE72C", command, RescanAsync),
            ManageCommandKind.ConvertLocalToSimulation =>
                Spec("转换本机到模拟", "Convert local to simulation", "\uE8AB", command, ConvertLocalAsync),
            ManageCommandKind.ImportSimulation =>
                Spec("导入模拟系统", "Import simulated system", "\uE8B5", command, ImportAsync),
            ManageCommandKind.ExportSimulation =>
                Spec("导出模拟系统", "Export simulated system", "\uEDE1", command, ExportAsync),
            ManageCommandKind.DeleteSimulation =>
                Spec("删除模拟系统", "Delete simulation", "\uE74D", command, DeleteSimulationAsync),
            ManageCommandKind.RenamePool =>
                Spec("重命名存储池", "Rename pool", "\uE8AC", command, NavigateEditAsync),
            ManageCommandKind.CreatePool =>
                Spec("创建存储池", "Create pool", "\uE710", command, NavigateEditAsync),
            ManageCommandKind.EditPool =>
                Spec("编辑存储池", "Edit pool", "\uE90F", command, NavigateEditAsync),
            ManageCommandKind.OptimizePoolUsage =>
                Spec("优化磁盘使用率", "Optimize disk usage", "\uE945", command, NavigateEditAsync),
            ManageCommandKind.RenameTier =>
                Spec("重命名存储层", "Rename tier", "\uE8AC", command, NavigateEditAsync),
            ManageCommandKind.CreateTier =>
                Spec("创建存储层", "Create tier", "\uE710", command, NavigateEditAsync),
            ManageCommandKind.EditTier =>
                Spec("编辑存储层", "Edit tier", "\uE90F", command, NavigateEditAsync),
            ManageCommandKind.RenameDisk =>
                Spec("重命名磁盘", "Rename disk", "\uE8AC", command, NavigateEditAsync),
            ManageCommandKind.InitializeDisk =>
                Spec("初始化磁盘", "Initialize disk", "\uE9CE", command, NavigateEditAsync),
            ManageCommandKind.CreatePartition =>
                Spec("新建分区", "New partition", "\uE710", command, NavigateEditAsync),
            ManageCommandKind.ConvertDiskStyle =>
                Spec("转换到其他类型", "Convert to another style", "\uE8AB", command, NavigateEditAsync),
            ManageCommandKind.OnlineDisk =>
                Spec("联机", "Online", "\uEDA2", command, NavigateEditAsync),
            ManageCommandKind.OfflineDisk =>
                Spec("脱机", "Offline", "\uEDA2", command, NavigateEditAsync),
            ManageCommandKind.ShowSystemProperties =>
                Spec("系统属性对话框", "System properties dialog", "\uE90A", command, PropertiesAsync),
            ManageCommandKind.OpenExplorer =>
                Spec("打开资源管理器", "Open in File Explorer", "\uE838", command, OpenPartitionAsync),
            ManageCommandKind.ChangeDriveLetter =>
                Spec("修改盘符和路径", "Change drive letter and paths", "\uE8B7", command, NavigateEditAsync),
            ManageCommandKind.RenamePartition =>
                Spec("重命名分区", "Rename partition", "\uE8AC", command, NavigateEditAsync),
            ManageCommandKind.FormatPartition =>
                Spec("格式化分区", "Format partition", "\uE9CE", command, NavigateEditAsync),
            ManageCommandKind.EditPartition =>
                Spec("编辑分区", "Edit partition", "\uE90F", command, NavigateEditAsync),
            ManageCommandKind.DeletePartition =>
                Spec("删除分区", "Delete partition", "\uE74D", command, NavigateEditAsync),
            ManageCommandKind.OptimizeDrive =>
                Spec("优化驱动器", "Optimize drive", "\uE945", command, OptimizeDrivesAsync),
            ManageCommandKind.ExportCategory =>
                new CommandSpec(
                    Text(
                        $"导出 [{ViewModel.SelectedCategoryTitle}] 信息列表",
                        $"Export [{ViewModel.SelectedCategoryTitle}] info list"),
                    "\uE8B6",
                    command.IsEnabled,
                    ExportListAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private CommandSpec Spec(
        string zh,
        string en,
        string glyph,
        ManageCommandView command,
        Func<Task> action) =>
        new(Text(zh, en), glyph, command.IsEnabled, action);


    private async Task DeleteSimulationAsync()
    {
        var l = ViewModel.Localization;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = l["Warning"],
            Content = l["ConfirmDeleteSimulation"],
            PrimaryButtonText = l["DeleteSimulation"],
            CloseButtonText = l["Cancel"],
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        await ViewModel.DeleteSimulationAsync();
    }

    private void BuildCommandButtons()
    {
        if (ViewModel is null)
        {
            return;
        }
        CommandButtonsPanel.Children.Clear();
        foreach (var spec in BuildCommandSpecs())
        {
            AddCommand(spec);
        }
    }

    private void ShowNodeContextMenu(
        ManageObjectTarget node,
        FrameworkElement element,
        Windows.Foundation.Point pointerPosition)
    {
        var selected = ViewModel.SelectedWorkspaceItem;
        if (selected?.Projection is null
            || selected.Projection.Id != node.Id
            || selected.Projection.Role != node.Role)
        {
            return;
        }

        var specs = BuildCommandSpecs();
        if (specs.Count == 0)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var spec in specs)
        {
            var item = new MenuFlyoutItem
            {
                Text = spec.Text,
                Icon = new FontIcon { FontSize = 14, Glyph = spec.Glyph },
                IsEnabled = spec.Enabled
            };
            item.Click += async (_, _) => await RunCommandAsync(spec.Action);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(element, new FlyoutShowOptions { Position = pointerPosition });
    }

    private void AddCommand(CommandSpec spec) => AddCommand(spec.Text, spec.Glyph, spec.Enabled, spec.Action);

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
        catch (Exception)
        {
            ViewModel.PresentNotification(WorkspaceNotificationFactory.OperationFailed(
                $"workspace-operation:{DateTimeOffset.UtcNow.Ticks}"));
        }
        BuildCommandButtons();
    }

    private async Task RescanAsync() => await ViewModel.ScanAsync();

    private async Task ExportListAsync()
    {
        var columns = ViewModel.ComparisonColumns;
        if (columns.Count == 0)
        {
            return;
        }

        var csv = ManageCategoryCsvExporter.Create(
            ViewModel.Localization["Name"],
            columns.Select(column => new ManageExportColumn(
                column.Name,
                column.Rows.Select(row => new ManageExportProperty(row.Label, row.Value)).ToArray()))
                .ToArray());

        var path = await new WinPool.App.Services.DesktopExportService().ExportCsvAsync(
            $"WinPool-{ViewModel.SelectedCategory}-{DateTime.Now:yyyyMMdd-HHmmss}",
            csv);
        if (path is not null)
        {
            ViewModel.PresentNotification(WorkspaceNotificationFactory.ExportCompleted(
                $"export-list:{DateTimeOffset.UtcNow.Ticks}"));
        }
    }

    private async Task ExportAsync()
    {
        if (await ViewModel.ExportActiveSystemAsync() is not null)
        {
            ViewModel.PresentNotification(WorkspaceNotificationFactory.ExportCompleted(
                $"export:{DateTimeOffset.UtcNow.Ticks}"));
        }
    }

    private async Task ImportAsync()
    {
        if (await ViewModel.ImportSystemAsync())
        {
            ViewModel.PresentNotification(WorkspaceNotificationFactory.ImportCompleted(
                $"import:{DateTimeOffset.UtcNow.Ticks}"));
        }
    }

    private Task NavigateEditAsync()
    {
        ((MainWindow)App.Window).ShowEdit(
            ViewModel.SelectedWorkspaceItem?.Projection?.Id.ProviderKey);
        return Task.CompletedTask;
    }

    private async Task ConvertLocalAsync()
    {
        await ViewModel.ConvertLocalToSimulationAsync();
        ViewModel.NotificationService.PublishInfo(
            Text("转换本机到模拟", "Convert local to simulation"),
            ViewModel.Localization["ConvertedToSimulation"],
            "workspace-operation",
            $"convert-local:{DateTimeOffset.UtcNow.Ticks}");
    }

    private void NotifyTargetMissing() =>
        ViewModel.NotificationService.PublishWarning(
            ViewModel.Localization["Warning"],
            ViewModel.Localization["TargetMissing"],
            "workspace-operation",
            $"target-missing:{DateTimeOffset.UtcNow.Ticks}");

    private async Task OptimizeDrivesAsync()
    {
        await Task.CompletedTask;
        var surface = ViewModel.GetSelectedCommandSurface();
        var target = surface?.SystemDialogTarget;
        if (target is null || !target.HasResolvedPartition)
        {
            NotifyTargetMissing();
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dfrgui.exe",
            Arguments = string.IsNullOrWhiteSpace(target.DriveLetter)
                ? string.Empty
                : $"{target.DriveLetter}:",
            UseShellExecute = true
        });
    }

    private async Task OpenPartitionAsync()
    {
        var target = ViewModel.GetSelectedCommandSurface()?.SystemDialogTarget;
        if (target is null || !target.HasResolvedPartition)
        {
            NotifyTargetMissing();
            return;
        }
        if (!Directory.Exists(target.PartitionPath))
        {
            NotifyTargetMissing();
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{target.PartitionPath}\"",
            UseShellExecute = true
        });
        await Task.CompletedTask;
    }

    private async Task PropertiesAsync()
    {
        await Task.CompletedTask;
        var role = ViewModel.SelectedWorkspaceItem?.Projection?.Role;
        var surface = ViewModel.GetSelectedCommandSurface();
        var target = surface?.SystemDialogTarget;
        if (target is null)
        {
            return;
        }

        if (role == ManageObjectRole.Partition)
        {
            if (!target.HasResolvedPartition)
            {
                NotifyTargetMissing();
                return;
            }
            if (Directory.Exists(target.PartitionPath)
                && !TryShowNativeProperties(target.PartitionPath))
            {
                ViewModel.NotificationService.PublishError(
                    ViewModel.Localization["Error"],
                    ViewModel.Localization["OperationFailed"],
                    "workspace-operation",
                    $"properties:{DateTimeOffset.UtcNow.Ticks}");
            }
            return;
        }

        if (role is not (ManageObjectRole.PhysicalDisk
            or ManageObjectRole.VirtualDisk
            or ManageObjectRole.OsDisk))
        {
            return;
        }
        if (!target.HasResolvedDisk)
        {
            NotifyTargetMissing();
            return;
        }
        if (ViewModel.AgentConnection is not null && target.DiskNumber is int diskNumber)
        {
            var response = await ViewModel.AgentConnection.SendAsync(
                new OpenAgentNativePropertiesRequest(
                    surface!.ObjectId,
                    diskNumber,
                    CorrelationId.New()),
                CancellationToken.None);
            if (response.IsSuccess)
            {
                foreach (var message in response.Messages)
                {
                    ViewModel.NotificationService.PublishWarning(
                        ViewModel.Localization["Warning"],
                        message.DiagnosticText,
                        "workspace-operation",
                        $"properties:{message.Code}");
                }
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(target.PhysicalDeviceInstanceId))
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("devmgr.dll,DeviceProperties_RunDLL");
            startInfo.ArgumentList.Add("/DeviceID");
            startInfo.ArgumentList.Add(target.PhysicalDeviceInstanceId);
            System.Diagnostics.Process.Start(startInfo);
            return;
        }
        if (target.UseDiskManagementFallback)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskmgmt.msc",
                UseShellExecute = true
            });
        }
    }


    private string Text(string zh, string en) =>
        ViewModel.Localization.IsChinese ? zh : en;

    private void TopologyScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(320, e.NewSize.Width - 20);
        TopologySystemsControl.Width = width;
        ViewModel.UpdateTopologyViewportWidth(width);
    }

    private void ViewModel_WorkspaceSelectionChanged(object? sender, EventArgs e)
    {
        if (ComputeTableSignature() != _renderedSignature)
        {
            RebuildComparisonTable();
        }
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ApplyColumnHighlight(centerSelected: true));
        BuildCommandButtons();
    }
}
