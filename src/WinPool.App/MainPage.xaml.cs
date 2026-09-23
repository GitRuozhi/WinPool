using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices;
using WinPool.App.ViewModels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool_App.Controls;

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

    private const double LabelColumnMaxWidth = PropertyTableVisuals.LabelColumnMaxWidth;
    private const double ColumnGap = PropertyTableVisuals.ColumnGap;
    private const double RowHeight = PropertyTableVisuals.RowHeight;
    private const double MaxValueWidth = PropertyTableVisuals.ValueColumnMaxWidth;
    private const double NameHeaderContentOverhead = 44; // 14 DIP icon + 6 DIP gap + 24 DIP button padding.
    private readonly Dictionary<string, int> _columnIndexByKey = new(StringComparer.Ordinal);
    private readonly List<Border> _columnCells = [];
    private readonly Dictionary<Border, TableCellContext> _tableCellContexts = [];
    private string? _hoveredColumnKey;
    private string _renderedSignature = string.Empty;

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    public MainPage()
    {
        InitializeComponent();
        LabelColumnDefinition.MaxWidth = LabelColumnMaxWidth;
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
        DispatcherQueue.TryEnqueue(() =>
            TopologyScrollViewer.ChangeView(
                ViewModel.TopologyHorizontalOffset,
                ViewModel.TopologyVerticalOffset,
                null,
                disableAnimation: true));
        RebuildComparisonTable();
        BuildCommandButtons();
        await ViewModel.WhenWorkspaceReady;
        RebuildComparisonTable();
        BuildCommandButtons();
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
        _tableCellContexts.Clear();
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
            var labelCell = PropertyTableVisuals.CreateCell(
                new TextBlock
                {
                    Padding = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = secondaryBrush,
                    IsTextSelectionEnabled = true,
                    MaxWidth = LabelColumnMaxWidth,
                    Text = labels[rowIndex],
                    TextTrimming = TextTrimming.CharacterEllipsis
                }, dividerBrush);
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
                    MaxWidth = isNameRow ? MaxValueWidth - NameHeaderContentOverhead : MaxValueWidth,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isNameRow ? FontWeights.SemiBold : FontWeights.Normal,
                    Text = value,
                    TextWrapping = TextWrapping.WrapWholeWords
                };
                FrameworkElement content = text;
                if (isNameRow)
                {
                    var selector = new Button
                    {
                        Style = (Style)Application.Current.Resources["WinPoolButtonBaseStyle"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Content = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Children =
                            {
                                new FontIcon { FontSize = 14, Glyph = "\uE76C" },
                                text
                            }
                        },
                        Tag = columns[i].Key
                    };
                    AutomationProperties.SetName(selector, columns[i].Name);
                    ContextHelp.Set(selector, Text(
                        "选择此列对象以查看其属性和可用操作。",
                        "Select this column's object to view its properties and available actions."));
                    selector.Click += ColumnHeader_Click;
                    text.Padding = new Thickness(0);
                    content = selector;
                }
                var cell = PropertyTableVisuals.CreateCell(
                    content,
                    dividerBrush,
                    i == 0 ? 0 : ColumnGap,
                    tag: columns[i].Key);
                cell.Tapped += ColumnCell_Tapped;
                cell.RightTapped += ColumnCell_RightTapped;
                cell.PointerEntered += ColumnCell_PointerEntered;
                cell.PointerExited += ColumnCell_PointerExited;
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
                _columnCells.Add(cell);
                _tableCellContexts[cell] = new(columns[i].Key, value);
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

    private void ColumnCell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border cell
            || !_tableCellContexts.TryGetValue(cell, out var context)
            || context.GroupKey != ViewModel.SelectedWorkspaceItem?.Key)
        {
            return;
        }

        var column = ViewModel.ComparisonColumns.FirstOrDefault(item => item.Key == context.GroupKey);
        if (column is null)
        {
            return;
        }

        PropertyTableContextMenu.Show(
            cell,
            e.GetPosition(cell),
            XamlRoot,
            new PropertyTableContextMenuRequest(
                context.Value,
                column.Name,
                column.Rows.Select(row => new PropertyTableCopyRow(row.Label, row.Value)).ToArray(),
                RawFieldsForComparisonGroup(context.GroupKey),
                ViewModel.Localization["CopyCurrentValue"],
                ViewModel.Localization["CopyGroup"],
                ViewModel.Localization["RawFields"],
                ViewModel.Localization["Close"]));
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e) =>
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
            var text = cell.Child switch
            {
                TextBlock direct => direct,
                Button { Content: StackPanel nested } => nested.Children.OfType<TextBlock>().FirstOrDefault(),
                _ => null
            };
            var icon = cell.Child is Button { Content: StackPanel iconContent }
                ? iconContent.Children.OfType<FontIcon>().FirstOrDefault()
                : null;
            if (text is not null)
            {
                if (isSelected)
                {
                    text.Foreground = accentForeground;
                }
                else
                {
                    text.ClearValue(TextBlock.ForegroundProperty);
                }
                text.IsTextSelectionEnabled = false;
            }

            if (icon is not null)
            {
                if (isSelected)
                {
                    icon.Foreground = accentForeground;
                }
                else
                {
                    icon.ClearValue(FontIcon.ForegroundProperty);
                }
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

    private sealed record CommandSpec(
        string Text,
        string Glyph,
        bool Enabled,
        Func<Task> Action,
        string Purpose,
        string? DisabledReason = null);

    private sealed record TableCellContext(string GroupKey, string Value);

    private string RawFieldsForComparisonGroup(string groupKey)
    {
        var workspaceItem = ViewModel.Objects.FirstOrDefault(item => item.Key == groupKey);
        if (workspaceItem?.Projection is null)
        {
            return ViewModel.Localization["RawFieldsUnavailable"];
        }

        var document = workspaceItem.StorageSystemId is null
            ? ViewModel.ActiveDocument
            : ViewModel.SystemCatalog.Find(workspaceItem.StorageSystemId);
        if (document?.Snapshot.FindSyntheticStorageObject(workspaceItem.Projection.Id.ProviderKey) is not null)
        {
            return ViewModel.Localization["NoOriginalSource"];
        }
        var system = document?.Unified;
        var source = system?.Objects.FirstOrDefault(item =>
            item.Id == workspaceItem.Projection.Id.ProviderKey
            || item.Sources.Any(observation => observation.Id == workspaceItem.Projection.Id.ProviderKey));
        return source is null || system is null
            ? ViewModel.Localization["RawFieldsUnavailable"]
            : WinPoolSourceDetails.Describe(system, source);
    }

    private List<CommandSpec> BuildCommandSpecs()
    {
        var selected = ViewModel.SelectedWorkspaceItem;
        if (selected?.IsAction == true)
        {
            return [new CommandSpec(
                Text("导入", "Import"),
                "\uE8B5",
                true,
                ImportAsync,
                Text("从 WinPool JSON 文件导入一个可编辑的模拟系统。", "Import a WinPool JSON file as an editable simulated system."))];
        }

        var surface = ViewModel.GetSelectedCommandSurface();
        var commands = surface is null
            ? new List<CommandSpec>()
            : surface.Commands.Select(command => BuildCommandSpec(command, surface)).ToList();
        return commands;
    }

    private List<CommandSpec> BuildActionPanelSpecs()
    {
        var selected = ViewModel.SelectedWorkspaceItem;
        if (selected?.IsAction == true)
        {
            return BuildCommandSpecs();
        }

        var surface = ViewModel.GetSelectedCommandSurface();
        if (surface is null)
        {
            return [];
        }

        return ViewModel.SelectedCategory switch
        {
            ManageWorkspaceCategory.Pool =>
            [
                AlwaysEnabledNavigationSpec(
                    ManageCommandKind.EditPool,
                    surface,
                    "编辑存储池",
                    "Edit pool",
                    NavigateStructureAsync,
                    Text(
                        "打开存储结构页并定位所选存储池；目标页会根据当前系统模式限制可执行的编辑操作。",
                        "Open Storage structure at the selected pool; the target page applies editing limits for the current system mode."))
            ],
            ManageWorkspaceCategory.Tier =>
            [
                AlwaysEnabledNavigationSpec(
                    ManageCommandKind.EditTier,
                    surface,
                    "编辑存储层",
                    "Edit tier",
                    NavigateStructureAsync,
                    Text(
                        "打开存储结构页并定位所选存储层所属的存储池，显示该池的存储层参数；目标页会根据当前系统模式限制可执行的编辑操作。",
                        "Open Storage structure at the selected tier's pool and show its tier settings; the target page applies editing limits for the current system mode."))
            ],
            ManageWorkspaceCategory.Disk =>
            [
                BuildDiskEditorSpec(surface),
                SurfaceCommandSpec(ManageCommandKind.ShowSystemProperties, surface)
            ],
            ManageWorkspaceCategory.Partition or ManageWorkspaceCategory.Volume =>
            [
                SurfaceCommandSpec(ManageCommandKind.OpenExplorer, surface),
                AlwaysEnabledNavigationSpec(
                    ManageCommandKind.EditPartition,
                    surface,
                    "编辑分区",
                    "Edit partition",
                    NavigatePartitionAsync,
                    Text(
                        "打开磁盘与分区页并定位所选分区；目标页会根据当前系统模式限制可执行的编辑操作。",
                        "Open Disk and partitions at the selected partition; the target page applies editing limits for the current system mode.")),
                SurfaceCommandSpec(ManageCommandKind.OptimizeDrive, surface),
                SurfaceCommandSpec(ManageCommandKind.ShowSystemProperties, surface)
            ],
            _ => BuildCommandSpecs()
        };
    }

    private CommandSpec BuildDiskEditorSpec(ManageCommandSurfaceView surface)
    {
        var stableId = ViewModel.SelectedWorkspaceItem?.Projection?.Id.ProviderKey;
        var snapshot = ViewModel.EffectiveActiveSnapshot;
        var visiblePartitionDiskIds = EditWorkspace.ProjectPartitionWorkspace(snapshot)
            .Select(node => node.Unit.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasPartitionView = snapshot.OsDisks.Any(disk =>
            visiblePartitionDiskIds.Contains(disk.StableId)
            && (StringComparer.OrdinalIgnoreCase.Equals(disk.StableId, stableId)
                || StringComparer.OrdinalIgnoreCase.Equals(disk.PhysicalDiskStableId, stableId)
                || StringComparer.OrdinalIgnoreCase.Equals(disk.VirtualDiskStableId, stableId)));
        var hasStructureView = snapshot.PhysicalDisks.Any(disk =>
                StringComparer.OrdinalIgnoreCase.Equals(disk.StableId, stableId)
                && snapshot.StoragePools.Any(pool =>
                    StringComparer.OrdinalIgnoreCase.Equals(pool.StableId, disk.PoolStableId)))
            || snapshot.VirtualDisks.Any(disk =>
                StringComparer.OrdinalIgnoreCase.Equals(disk.StableId, stableId)
                && snapshot.StoragePools.Any(pool =>
                    StringComparer.OrdinalIgnoreCase.Equals(pool.StableId, disk.PoolStableId)));
        var purpose = hasPartitionView
            ? Text(
                "打开磁盘与分区页并定位所选磁盘；目标页会根据当前系统模式限制可执行的编辑操作。",
                "Open Disk and partitions at the selected disk; the target page applies editing limits for the current system mode.")
            : Text(
                "打开存储结构页并定位所选磁盘的存储池；目标页会根据当前系统模式限制可执行的编辑操作。",
                "Open Storage structure at the selected disk's pool; the target page applies editing limits for the current system mode.");
        var spec = AlwaysEnabledNavigationSpec(
            ManageCommandKind.RenameDisk,
            surface,
            "编辑磁盘",
            "Edit disk",
            hasPartitionView ? NavigatePartitionAsync : NavigateStructureAsync,
            purpose) with { Glyph = "\uE90F" };
        return hasPartitionView || hasStructureView
            ? spec
            : spec with
            {
                Enabled = false,
                DisabledReason = Text(
                    "当前磁盘没有可定位的分区或存储结构编辑视图。",
                    "This disk has no partition or storage-structure editor target.")
            };
    }

    private CommandSpec AlwaysEnabledNavigationSpec(
        ManageCommandKind kind,
        ManageCommandSurfaceView surface,
        string zh,
        string en,
        Func<Task> action,
        string purpose)
    {
        var command = surface.Commands.FirstOrDefault(candidate => candidate.Kind == kind)
            ?? new ManageCommandView(kind, true);
        return BuildCommandSpec(command, surface) with
        {
            Text = Text(zh, en),
            Enabled = true,
            Action = action,
            Purpose = purpose,
            DisabledReason = null
        };
    }

    private CommandSpec SurfaceCommandSpec(
        ManageCommandKind kind,
        ManageCommandSurfaceView surface)
    {
        var command = surface.Commands.FirstOrDefault(candidate => candidate.Kind == kind)
            ?? new ManageCommandView(kind, false);
        return BuildCommandSpec(command, surface);
    }

    private CommandSpec BuildCommandSpec(
        ManageCommandView command,
        ManageCommandSurfaceView surface)
    {
        var spec = command.Kind switch
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
                Spec("重命名存储池", "Rename pool", "\uE8AC", command, NavigateStructureAsync),
            ManageCommandKind.CreatePool =>
                Spec("创建存储池", "Create pool", "\uE710", command, NavigateStructureAsync),
            ManageCommandKind.EditPool =>
                Spec("编辑存储池", "Edit pool", "\uE90F", command, NavigateStructureAsync),
            ManageCommandKind.OptimizePoolUsage =>
                Spec("优化磁盘使用率", "Optimize disk usage", "\uE945", command, NavigateStructureAsync),
            ManageCommandKind.RenameTier =>
                Spec("重命名存储层", "Rename tier", "\uE8AC", command, NavigateStructureAsync),
            ManageCommandKind.CreateTier =>
                Spec("创建存储层", "Create tier", "\uE710", command, NavigateStructureAsync),
            ManageCommandKind.EditTier =>
                Spec("编辑存储层", "Edit tier", "\uE90F", command, NavigateStructureAsync),
            ManageCommandKind.RenameDisk =>
                Spec("重命名磁盘", "Rename disk", "\uE8AC", command, RenameDiskAsync),
            ManageCommandKind.InitializeDisk =>
                Spec("初始化磁盘", "Initialize disk", "\uE9CE", command, NavigatePartitionAsync),
            ManageCommandKind.CreatePartition =>
                Spec("新建分区", "New partition", "\uE710", command, NavigatePartitionAsync),
            ManageCommandKind.ConvertDiskStyle =>
                Spec("转换为 GPT", "Convert to GPT", "\uE8AB", command, NavigatePartitionAsync),
            ManageCommandKind.OnlineDisk =>
                Spec("联机", "Online", "\uEDA2", command, NavigatePartitionAsync),
            ManageCommandKind.OfflineDisk =>
                Spec("脱机", "Offline", "\uEDA2", command, NavigatePartitionAsync),
            ManageCommandKind.ShowSystemProperties =>
                Spec("系统属性对话框", "System properties dialog", "\uE90A", command, PropertiesAsync),
            ManageCommandKind.OpenExplorer =>
                Spec("打开资源管理器", "Open in File Explorer", "\uE838", command, OpenPartitionAsync),
            ManageCommandKind.ChangeDriveLetter =>
                Spec("修改盘符和路径", "Change drive letter and paths", "\uE8B7", command, NavigatePartitionAsync),
            ManageCommandKind.RenamePartition =>
                Spec("修改卷标", "Change volume label", "\uE8AC", command, NavigatePartitionAsync),
            ManageCommandKind.FormatPartition =>
                Spec("格式化分区", "Format partition", "\uE9CE", command, NavigatePartitionAsync),
            ManageCommandKind.EditPartition =>
                Spec("编辑分区", "Edit partition", "\uE90F", command, NavigatePartitionAsync),
            ManageCommandKind.DeletePartition =>
                Spec("删除分区", "Delete partition", "\uE74D", command, NavigatePartitionAsync),
            ManageCommandKind.OptimizeDrive =>
                Spec("优化驱动器", "Optimize drive", "\uE945", command, OptimizeDrivesAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        return command.IsEnabled
            ? spec
            : spec with { DisabledReason = ManageDisabledReason(command.Kind, surface) };
    }

    private CommandSpec Spec(
        string zh,
        string en,
        string glyph,
        ManageCommandView command,
        Func<Task> action) =>
        new(
            Text(zh, en),
            glyph,
            command.IsEnabled,
            action,
            CommandPurpose(command.Kind));

    private string CommandPurpose(ManageCommandKind kind) => kind switch
    {
        ManageCommandKind.RefreshLocal => Text(
            "重新扫描本机存储并刷新只读信息。",
            "Rescan this computer's storage and refresh its read-only information."),
        ManageCommandKind.ConvertLocalToSimulation => Text(
            "将当前本机清单复制为一个可编辑的模拟系统；不会修改真实磁盘。",
            "Copy the current local inventory into an editable simulation without changing real disks."),
        ManageCommandKind.ImportSimulation => Text(
            "从 WinPool JSON 文件导入一个可编辑的模拟系统。",
            "Import a WinPool JSON file as an editable simulated system."),
        ManageCommandKind.ExportSimulation => Text(
            "将当前系统保存为 WinPool JSON 文件；不会修改系统。",
            "Save the current system as a WinPool JSON file without changing it."),
        ManageCommandKind.DeleteSimulation => Text(
            "删除当前模拟系统及其保存的数据；不会影响真实磁盘。",
            "Delete the current simulation and its saved data without affecting real disks."),
        ManageCommandKind.RenamePool => Text(
            "打开存储结构页以修改所选模拟存储池的名称。",
            "Open Storage structure to change the selected simulated storage pool name."),
        ManageCommandKind.CreatePool => Text(
            "打开存储结构页以在模拟系统中创建存储池。",
            "Open Storage structure to create a storage pool in the simulation."),
        ManageCommandKind.EditPool => Text(
            "打开存储结构页以编辑所选模拟存储池。",
            "Open Storage structure to edit the selected simulated storage pool."),
        ManageCommandKind.OptimizePoolUsage => Text(
            "打开存储结构页以查看模拟存储池的使用率操作。",
            "Open Storage structure for simulated storage-pool usage actions."),
        ManageCommandKind.RenameTier => Text(
            "打开存储结构页以修改所选模拟存储层的名称。",
            "Open Storage structure to change the selected simulated storage tier name."),
        ManageCommandKind.CreateTier => Text(
            "打开存储结构页以在模拟系统中创建存储层。",
            "Open Storage structure to create a storage tier in the simulation."),
        ManageCommandKind.EditTier => Text(
            "打开存储结构页以编辑所选模拟存储层。",
            "Open Storage structure to edit the selected simulated storage tier."),
        ManageCommandKind.RenameDisk => Text(
            "输入新名称以修改所选模拟磁盘；不会修改真实磁盘。",
            "Enter a new name for the selected simulated disk without changing real disks."),
        ManageCommandKind.InitializeDisk => Text(
            "打开磁盘与分区页以初始化所选模拟磁盘。",
            "Open Disk and partitions to initialize the selected simulated disk."),
        ManageCommandKind.CreatePartition => Text(
            "打开磁盘与分区页以在所选模拟磁盘上创建分区。",
            "Open Disk and partitions to create a partition on the selected simulated disk."),
        ManageCommandKind.ConvertDiskStyle => Text(
            "打开磁盘与分区页以将所选模拟磁盘转换为 GPT。",
            "Open Disk and partitions to convert the selected simulated disk to GPT."),
        ManageCommandKind.OnlineDisk => Text(
            "打开磁盘与分区页以使所选模拟磁盘联机。",
            "Open Disk and partitions to bring the selected simulated disk online."),
        ManageCommandKind.OfflineDisk => Text(
            "打开磁盘与分区页以使所选模拟磁盘脱机。",
            "Open Disk and partitions to take the selected simulated disk offline."),
        ManageCommandKind.ShowSystemProperties => Text(
            "打开 Windows 的系统属性对话框；不会修改存储。",
            "Open the Windows system-properties dialog without changing storage."),
        ManageCommandKind.OpenExplorer => Text(
            "在文件资源管理器中打开所选本机分区。",
            "Open the selected local partition in File Explorer."),
        ManageCommandKind.ChangeDriveLetter => Text(
            "打开磁盘与分区页以修改所选模拟分区的盘符和路径。",
            "Open Disk and partitions to change the selected simulated partition's drive letter and paths."),
        ManageCommandKind.RenamePartition => Text(
            "打开磁盘与分区页以修改所选模拟分区的卷标。",
            "Open Disk and partitions to change the selected simulated partition's volume label."),
        ManageCommandKind.FormatPartition => Text(
            "打开磁盘与分区页以格式化所选模拟分区。",
            "Open Disk and partitions to format the selected simulated partition."),
        ManageCommandKind.EditPartition => Text(
            "打开磁盘与分区页以编辑所选模拟分区。",
            "Open Disk and partitions to edit the selected simulated partition."),
        ManageCommandKind.DeletePartition => Text(
            "打开磁盘与分区页以删除所选模拟分区。",
            "Open Disk and partitions to delete the selected simulated partition."),
        ManageCommandKind.OptimizeDrive => Text(
            "打开 Windows 的驱动器优化工具；不会模拟性能结果。",
            "Open Windows Optimize Drives; it does not claim a simulated performance result."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private string ManageDisabledReason(
        ManageCommandKind kind,
        ManageCommandSurfaceView surface)
    {
        var target = ViewModel.SelectedWorkspaceItem?.Projection;
        if (target is null)
        {
            return Text(
                "当前没有可用于此操作的管理对象。",
                "There is no selected management object for this operation.");
        }

        // These system-level commands have a useful explanation before the
        // general local-read-only guard. They are disabled because the active
        // document has a different kind, not because a selected disk failed a
        // storage precondition.
        switch (kind)
        {
            case ManageCommandKind.RefreshLocal:
                return Text(
                    "只有本机系统可以重新扫描；模拟系统没有可扫描的本机存储。",
                    "Only the local system can be rescanned; a simulation has no local storage to scan.");
            case ManageCommandKind.ConvertLocalToSimulation:
                return Text(
                    "当前已是模拟系统，无需再次转换。",
                    "The current system is already a simulation and does not need conversion.");
            case ManageCommandKind.DeleteSimulation when ViewModel.IsLocalSystem:
                return Text(
                    "本机存储不能删除；只能删除模拟系统。",
                    "The local system cannot be deleted; only a simulation can be deleted.");
        }

        if (kind is ManageCommandKind.OpenExplorer or ManageCommandKind.OptimizeDrive)
        {
            if (!ViewModel.IsLocalSystem)
            {
                return Text(
                    "此功能只打开或优化本机卷；模拟系统没有可交给 Windows 的本机路径。",
                    "This action only opens or optimizes a local volume; simulations have no local Windows path.");
            }

            return LocalPartitionTargetReason(kind, target);
        }

        if (kind == ManageCommandKind.ShowSystemProperties)
        {
            return !ViewModel.IsLocalSystem
                ? Text(
                    "Windows 系统属性对话框只可用于本机对象；模拟系统没有本机 Windows 目标。",
                    "The Windows properties dialog is only available for a local object; simulations have no local Windows target.")
                : LocalWindowsTargetReason(target);
        }

        if (ViewModel.IsLocalSystem)
        {
            return LocalReadOnlyReason(kind);
        }

        if (IsSyntheticOrUnstableTarget(target))
        {
            return Text(
                "此对象是派生汇总项，或没有稳定的 Windows 来源；不能直接执行该操作。",
                "This is a derived item or it lacks a stable Windows source, so the operation cannot be applied directly.");
        }

        return kind switch
        {
            ManageCommandKind.RenamePool => PoolOperationReason(target, "重命名", "renamed"),
            ManageCommandKind.CreatePool => PoolOperationReason(target, "创建新池", "used to create a new pool"),
            ManageCommandKind.EditPool => PoolOperationReason(target, "编辑", "edited"),
            ManageCommandKind.OptimizePoolUsage => PoolOperationReason(target, "优化", "optimized"),
            ManageCommandKind.RenameTier or ManageCommandKind.CreateTier or ManageCommandKind.EditTier => Text(
                "所选存储层没有可编辑的模拟存储来源。",
                "The selected storage tier has no editable simulated-storage source."),
            ManageCommandKind.RenameDisk => DiskRenameReason(target),
            ManageCommandKind.InitializeDisk => DiskInitializeReason(target, surface),
            ManageCommandKind.CreatePartition => DiskCreatePartitionReason(target, surface),
            ManageCommandKind.ConvertDiskStyle => DiskConvertReason(target, surface),
            ManageCommandKind.OnlineDisk => Text(
                "只有已识别为脱机状态的模拟 Windows 磁盘可以联机。",
                "Only a simulated Windows disk identified as offline can be brought online."),
            ManageCommandKind.OfflineDisk => DiskOfflineReason(target, surface),
            ManageCommandKind.ShowSystemProperties => LocalWindowsTargetReason(target),
            ManageCommandKind.ChangeDriveLetter => PartitionEditReason(target, "修改盘符和路径", "change drive letters or paths"),
            ManageCommandKind.RenamePartition => PartitionEditReason(target, "修改卷标", "change its volume label"),
            ManageCommandKind.EditPartition => PartitionEditReason(target, "编辑", "be edited"),
            ManageCommandKind.FormatPartition => PartitionDestructiveReason(target, surface, "格式化", "formatted"),
            ManageCommandKind.DeletePartition => PartitionDestructiveReason(target, surface, "删除", "deleted"),
            ManageCommandKind.DeleteSimulation => Text(
                "只有当前模拟系统可删除。",
                "Only the currently selected simulation can be deleted."),
            _ => Text(
                "当前选择没有满足此操作的可编辑来源；请查看对象详情或选择可编辑的模拟对象。",
                "The selected object has no editable source that meets this operation's prerequisites. Inspect its details or select an editable simulation.")
        };
    }

    private string LocalReadOnlyReason(ManageCommandKind kind) => kind switch
    {
        ManageCommandKind.FormatPartition => Text(
            "本机存储为只读；WinPool 不会格式化真实分区。",
            "Local storage is read-only; WinPool will not format a real partition."),
        ManageCommandKind.DeletePartition => Text(
            "本机存储为只读；WinPool 不会删除真实分区。",
            "Local storage is read-only; WinPool will not delete a real partition."),
        ManageCommandKind.InitializeDisk => Text(
            "本机存储为只读；WinPool 不会初始化真实磁盘。",
            "Local storage is read-only; WinPool will not initialize a real disk."),
        ManageCommandKind.ConvertDiskStyle => Text(
            "本机存储为只读；WinPool 不会转换真实磁盘的分区样式。",
            "Local storage is read-only; WinPool will not convert a real disk's partition style."),
        ManageCommandKind.OnlineDisk or ManageCommandKind.OfflineDisk => Text(
            "本机存储为只读；WinPool 不会改变真实磁盘的联机状态。",
            "Local storage is read-only; WinPool will not change a real disk's online state."),
        ManageCommandKind.CreatePartition => Text(
            "本机存储为只读；WinPool 不会在真实磁盘上创建分区。",
            "Local storage is read-only; WinPool will not create a partition on a real disk."),
        _ => Text(
            "本机存储为只读；请转换或选择模拟系统后编辑。",
            "Local storage is read-only; convert it or select a simulated system to edit.")
    };

    private string LocalPartitionTargetReason(
        ManageCommandKind kind,
        ManageObjectListItemView target)
    {
        if (!IsPrimaryDataPartition(target))
        {
            return kind == ManageCommandKind.OpenExplorer
                ? Text(
                    "只有普通主分区或基本数据分区有可打开的本机卷路径。",
                    "Only a primary or basic-data partition has a local volume path that can be opened.")
                : Text(
                    "只有普通主分区或基本数据分区可以交给 Windows 优化工具。",
                    "Only a primary or basic-data partition can be sent to Windows Optimize Drives.");
        }

        return Text(
            "当前选择没有可用的本机 Windows 分区目标。",
            "The current selection has no usable local Windows partition target.");
    }

    private string LocalWindowsTargetReason(ManageObjectListItemView target)
    {
        if (target.Category is ManageWorkspaceCategory.Partition or ManageWorkspaceCategory.Volume
            && !IsPrimaryDataPartition(target))
        {
            return Text(
                "只有普通主分区或基本数据分区可以打开 Windows 属性对话框。",
                "Only a primary or basic-data partition can open the Windows properties dialog.");
        }

        return Text(
            "当前选择没有可用的本机 Windows 对话框目标。",
            "The current selection has no usable local Windows dialog target.");
    }

    private string PoolOperationReason(
        ManageObjectListItemView target,
        string zhOperation,
        string enOperation)
    {
        if (MetadataIsTrue(target, "isPrimordial"))
        {
            return Text(
                $"原始存储池不能{zhOperation}。",
                $"The primordial storage pool cannot be {enOperation}.");
        }

        return Text(
            "未解析到可编辑的普通模拟存储池。",
            "No editable non-primordial simulated storage pool was resolved.");
    }

    private string DiskRenameReason(ManageObjectListItemView target)
    {
        if (!IsDirectDiskRole(target.Role))
        {
            return Text(
                "只有直接的物理磁盘、虚拟磁盘或 Windows 磁盘可以重命名。",
                "Only a direct physical disk, virtual disk, or Windows disk can be renamed.");
        }

        return Text(
            "所选模拟磁盘没有可编辑的稳定 Windows 来源。",
            "The selected simulated disk has no editable stable Windows source.");
    }

    private string DiskInitializeReason(
        ManageObjectListItemView target,
        ManageCommandSurfaceView surface)
    {
        if (!IsDirectDiskRole(target.Role))
        {
            return DiskSourceReason();
        }
        if (HasCommand(surface, ManageCommandKind.OnlineDisk))
        {
            return Text(
                "该模拟磁盘当前脱机；请先联机后再初始化。",
                "This simulated disk is offline; bring it online before initializing it.");
        }
        if (IsCommandEnabled(surface, ManageCommandKind.ConvertDiskStyle))
        {
            return Text(
                "只有 RAW 模拟磁盘可以初始化；所选 MBR 磁盘可先转换为 GPT。",
                "Only a RAW simulated disk can be initialized; the selected MBR disk can be converted to GPT instead.");
        }
        if (IsCommandEnabled(surface, ManageCommandKind.CreatePartition))
        {
            return Text(
                "只有 RAW 模拟磁盘可以初始化；所选磁盘已经是 GPT。",
                "Only a RAW simulated disk can be initialized; the selected disk is already GPT.");
        }
        if (IsProtectedOrUnresolvedDisk(surface))
        {
            return Text(
                "该磁盘受启动/系统保护，或没有 Windows 磁盘视图；不能初始化。",
                "This disk is boot/system-protected or has no Windows disk view, so it cannot be initialized.");
        }

        return Text(
            "初始化需要一个联机的 RAW 模拟 Windows 磁盘。",
            "Initialization requires an online RAW simulated Windows disk.");
    }

    private string DiskCreatePartitionReason(
        ManageObjectListItemView target,
        ManageCommandSurfaceView surface)
    {
        if (!IsDirectDiskRole(target.Role))
        {
            return DiskSourceReason();
        }
        if (HasCommand(surface, ManageCommandKind.OnlineDisk))
        {
            return Text(
                "该模拟磁盘当前脱机；请先联机后再创建分区。",
                "This simulated disk is offline; bring it online before creating a partition.");
        }
        if (IsCommandEnabled(surface, ManageCommandKind.InitializeDisk))
        {
            return Text(
                "该模拟磁盘还是 RAW；请先初始化为 GPT 后再创建分区。",
                "This simulated disk is still RAW; initialize it as GPT before creating a partition.");
        }
        if (IsCommandEnabled(surface, ManageCommandKind.ConvertDiskStyle))
        {
            return Text(
                "该模拟磁盘是 MBR；请先转换为 GPT 后再创建分区。",
                "This simulated disk is MBR; convert it to GPT before creating a partition.");
        }
        if (IsProtectedOrUnresolvedDisk(surface))
        {
            return Text(
                "该磁盘受启动/系统保护，或没有 Windows 磁盘视图；不能创建分区。",
                "This disk is boot/system-protected or has no Windows disk view, so a partition cannot be created.");
        }

        return Text(
            "创建分区需要一个联机的 GPT 模拟 Windows 磁盘。",
            "Creating a partition requires an online GPT simulated Windows disk.");
    }

    private string DiskConvertReason(
        ManageObjectListItemView target,
        ManageCommandSurfaceView surface)
    {
        if (!IsDirectDiskRole(target.Role))
        {
            return DiskSourceReason();
        }
        if (HasCommand(surface, ManageCommandKind.OnlineDisk))
        {
            return Text(
                "该模拟磁盘当前脱机；请先联机后再转换分区样式。",
                "This simulated disk is offline; bring it online before converting its partition style.");
        }
        if (IsCommandEnabled(surface, ManageCommandKind.InitializeDisk))
        {
            return Text(
                "只有 MBR 模拟磁盘可以转换为 GPT；所选磁盘仍是 RAW。",
                "Only an MBR simulated disk can be converted to GPT; the selected disk is still RAW.");
        }
        if (IsCommandEnabled(surface, ManageCommandKind.CreatePartition))
        {
            return Text(
                "只有 MBR 模拟磁盘需要转换；所选磁盘已经是 GPT。",
                "Only an MBR simulated disk needs conversion; the selected disk is already GPT.");
        }
        if (IsProtectedOrUnresolvedDisk(surface))
        {
            return Text(
                "该磁盘受启动/系统保护，或没有 Windows 磁盘视图；不能转换分区样式。",
                "This disk is boot/system-protected or has no Windows disk view, so its partition style cannot be converted.");
        }

        return Text(
            "转换为 GPT 需要一个联机的非系统 MBR 模拟 Windows 磁盘。",
            "Converting to GPT requires an online, non-system MBR simulated Windows disk.");
    }

    private string DiskOfflineReason(
        ManageObjectListItemView target,
        ManageCommandSurfaceView surface)
    {
        if (!IsDirectDiskRole(target.Role))
        {
            return DiskSourceReason();
        }
        if (IsCommandEnabled(surface, ManageCommandKind.CreatePartition))
        {
            return Text(
                "该启动或系统磁盘不能脱机。",
                "This boot or system disk cannot be taken offline.");
        }
        if (IsProtectedOrUnresolvedDisk(surface))
        {
            return Text(
                "该磁盘受启动/系统保护，或没有 Windows 磁盘视图；不能脱机。",
                "This disk is boot/system-protected or has no Windows disk view, so it cannot be taken offline.");
        }

        return Text(
            "只有联机的模拟 Windows 磁盘可以脱机。",
            "Only an online simulated Windows disk can be taken offline.");
    }

    private string PartitionEditReason(
        ManageObjectListItemView target,
        string zhOperation,
        string enOperation)
    {
        if (!IsPrimaryDataPartition(target))
        {
            return Text(
                $"只有普通主分区或基本数据分区可以{zhOperation}。",
                $"Only a primary or basic-data partition can {enOperation}.");
        }

        return Text(
            "所选分区所在的模拟磁盘已脱机，或该分区没有可编辑的 Windows 来源。",
            "The selected partition's simulated disk is offline or the partition has no editable Windows source.");
    }

    private string PartitionDestructiveReason(
        ManageObjectListItemView target,
        ManageCommandSurfaceView surface,
        string zhOperation,
        string enOperation)
    {
        if (IsPartitionEditEnabled(surface))
        {
            return Text(
                $"所选分区是启动或系统分区，不能{zhOperation}。",
                $"The selected partition is a boot or system partition and cannot be {enOperation}.");
        }
        if (!IsPrimaryDataPartition(target))
        {
            return Text(
                $"只有普通主分区或基本数据分区可以{zhOperation}。",
                $"Only a primary or basic-data partition can be {enOperation}.");
        }

        return Text(
            "所选分区所在的模拟磁盘已脱机，或该分区没有可编辑的 Windows 来源。",
            "The selected partition's simulated disk is offline or the partition has no editable Windows source.");
    }

    private string DiskSourceReason() => Text(
        "所选对象没有可操作的物理、虚拟或 Windows 磁盘来源。",
        "The selected object has no operable physical, virtual, or Windows disk source.");

    private static bool IsSyntheticOrUnstableTarget(ManageObjectListItemView target) =>
        !target.IsStableIdentity
        || target.Role is ManageObjectRole.SyntheticStoragePool or ManageObjectRole.SyntheticStorageTier
        || target.Metadata.TryGetValue("hasOriginalSource", out var originalSource)
           && string.Equals(originalSource, "False", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectDiskRole(ManageObjectRole role) =>
        role is ManageObjectRole.PhysicalDisk or ManageObjectRole.VirtualDisk or ManageObjectRole.OsDisk;

    private static bool IsPrimaryDataPartition(ManageObjectListItemView target) =>
        target.Metadata.TryGetValue("partitionType", out var type)
        && type is "Primary" or "BasicData";

    private static bool MetadataIsTrue(ManageObjectListItemView target, string key) =>
        target.Metadata.TryGetValue(key, out var value)
        && bool.TryParse(value, out var result)
        && result;

    private static bool HasCommand(ManageCommandSurfaceView surface, ManageCommandKind kind) =>
        surface.Commands.Any(command => command.Kind == kind);

    private static bool IsCommandEnabled(ManageCommandSurfaceView surface, ManageCommandKind kind) =>
        surface.Commands.Any(command => command.Kind == kind && command.IsEnabled);

    private static bool IsProtectedOrUnresolvedDisk(ManageCommandSurfaceView surface) =>
        HasCommand(surface, ManageCommandKind.OfflineDisk)
        && !IsCommandEnabled(surface, ManageCommandKind.OfflineDisk)
        && IsCommandEnabled(surface, ManageCommandKind.RenameDisk);

    private static bool IsPartitionEditEnabled(ManageCommandSurfaceView surface) =>
        IsCommandEnabled(surface, ManageCommandKind.ChangeDriveLetter)
        || IsCommandEnabled(surface, ManageCommandKind.RenamePartition)
        || IsCommandEnabled(surface, ManageCommandKind.EditPartition);


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
        if (await DialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }
        var deletedTarget = OperationTarget();
        var deletedSystemId = ViewModel.SelectedSystem.Id;
        await ViewModel.DeleteSimulationAsync();
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Info,
            Text("模拟系统已删除", "Simulated system deleted"),
            $"{deletedTarget}{Environment.NewLine}{Text("已删除所选模拟系统。", "The selected simulated system was deleted.")}",
            "workspace-operation",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "workspace.delete-simulation.completed",
                Code = "workspace.delete-simulation.completed",
                SystemId = deletedSystemId,
                Target = deletedTarget,
                Detail = deletedTarget
            });
    }

    private void BuildCommandButtons()
    {
        if (ViewModel is null)
        {
            return;
        }
        CommandButtonsPanel.Children.Clear();
        var specs = BuildActionPanelSpecs();
        foreach (var spec in specs)
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

    private void AddCommand(CommandSpec spec) =>
        AddCommand(spec.Text, spec.Glyph, spec.Enabled, spec.Action, spec.Purpose, spec.DisabledReason);

    private void AddCommand(
        string text,
        string glyph,
        bool enabled,
        Func<Task> action,
        string purpose,
        string? disabledReason)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["WinPoolButtonBaseStyle"],
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
        ContextHelp.Set(button, purpose);
        if (!enabled && !string.IsNullOrWhiteSpace(disabledReason))
        {
            ContextHelp.SetDisabledReason(button, disabledReason);
        }
        button.Click += async (_, _) => await RunCommandAsync(action);
        // Disabled WinUI controls do not reliably receive pointer input. Keep
        // their explanatory help on the transparent host rather than adding a
        // permanent grey status sentence beside unrelated commands.
        CommandButtonsPanel.Children.Add(enabled ? button : ContextHelp.Wrap(button));
    }

    private async Task RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ViewModel.NotificationService.Publish(
                GlobalNotificationSeverity.Error,
                Text("操作失败", "Operation failed"),
                $"{OperationTarget()}{Environment.NewLine}{Text("操作未完成。请检查当前选择、连接或权限后重试。", "The operation did not complete. Check the current selection, connection, or permissions, then try again.")}",
                "workspace-operation",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = $"workspace-operation:{exception.GetType().Name}",
                    Code = "workspace.operation.exception",
                    SystemId = ViewModel.SelectedSystem.Id,
                    Target = OperationTarget(),
                    Detail = $"{exception.GetType().Name}: {exception.Message}"
                });
        }
        BuildCommandButtons();
    }

    private async Task RescanAsync() => await ViewModel.ScanAsync();

    private async Task ExportAsync()
    {
        if (await ViewModel.ExportActiveSystemAsync() is not null)
        {
            PublishWorkspaceInfo(
                Text("导出模拟系统", "Export simulated system"),
                Text("模拟系统已导出。", "The simulated system was exported."),
                "workspace.export.completed");
        }
    }

    private async Task ImportAsync()
    {
        if (await ViewModel.ImportSystemAsync())
        {
            PublishWorkspaceInfo(
                Text("导入模拟系统", "Import simulated system"),
                Text("模拟系统已导入。", "The simulated system was imported."),
                "workspace.import.completed");
        }
    }

    private Task NavigateStructureAsync()
    {
        ((MainWindow)App.Window).ShowStorageStructure(
            ViewModel.SelectedWorkspaceItem?.Projection?.Id.ProviderKey);
        return Task.CompletedTask;
    }

    private Task NavigatePartitionAsync()
    {
        var selected = ViewModel.SelectedWorkspaceItem?.Projection;
        var target = selected?.Role == ManageObjectRole.Volume
            ? ManageSelectionRules.ResolvePartition(ViewModel.ActiveDocument.Snapshot, selected.Id.ProviderKey, selected.Role)?.StableId
            : selected?.Id.ProviderKey;
        ((MainWindow)App.Window).ShowDiskPartition(target);
        return Task.CompletedTask;
    }

    private async Task RenameDiskAsync()
    {
        var selected = ViewModel.SelectedWorkspaceItem?.Projection;
        if (selected is null || string.IsNullOrWhiteSpace(selected.Id.ProviderKey))
        {
            NotifyTargetMissing();
            return;
        }

        // The selected Application projection already carries the visible name;
        // do not reach back into the domain snapshot from this UI adapter.
        var targetId = selected.Id.ProviderKey;
        var currentName = selected.DisplayName;
        var input = new TextBox
        {
            Text = currentName,
            Header = Text("新名称", "New name"),
            MinWidth = 320
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = Text("重命名磁盘", "Rename disk"),
            Content = input,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await DialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, currentName, StringComparison.Ordinal))
        {
            return;
        }

        var result = await ViewModel.ApplySimulationOperationAsync(
            new SimulationEditRequest(SimulationEditKind.Rename, targetId, Name: name));
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Messages.FirstOrDefault()?.UserTextKey
                ?? Text("模拟磁盘名称未保存。", "The simulated disk name was not saved."));
        }

        PublishWorkspaceInfo(
            Text("模拟磁盘已重命名", "Simulated disk renamed"),
            Text("已保存所选模拟磁盘的新名称。", "The selected simulated disk name was saved."),
            "workspace.rename-disk.completed");
    }

    private async Task ConvertLocalAsync()
    {
        await ViewModel.ConvertLocalToSimulationAsync();
        PublishWorkspaceInfo(
            Text("转换本机到模拟", "Convert local to simulation"),
            ViewModel.Localization["ConvertedToSimulation"],
            "workspace.convert.completed");
    }

    private void NotifyTargetMissing() =>
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Warning,
            ViewModel.Localization["Warning"],
            $"{OperationTarget()}{Environment.NewLine}{ViewModel.Localization["TargetMissing"]}",
            "workspace-operation",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "workspace.target-missing",
                Code = "workspace.target-missing",
                SystemId = ViewModel.SelectedSystem.Id,
                Target = OperationTarget(),
                Detail = ViewModel.Localization["TargetMissing"]
            });

    private string OperationTarget() => ViewModel.IsUsingSimulatedInventory
        ? Text($"模拟目标：{ViewModel.SelectedSystem.DisplayName}",
            $"Simulated target: {ViewModel.SelectedSystem.DisplayName}")
        : Text($"本机目标：{ViewModel.SelectedSystem.DisplayName}",
            $"Local target: {ViewModel.SelectedSystem.DisplayName}");

    private void PublishWorkspaceInfo(string title, string message, string code) =>
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Info,
            title,
            $"{OperationTarget()}{Environment.NewLine}{message}",
            "workspace-operation",
            new GlobalNotificationOptions
            {
                OccurrenceKey = $"{code}:{ViewModel.SelectedSystem.Id}",
                Code = code,
                SystemId = ViewModel.SelectedSystem.Id,
                Target = OperationTarget(),
                Detail = message
            });

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

        if (role is ManageObjectRole.Partition or ManageObjectRole.Volume)
        {
            if (!target.HasResolvedPartition)
            {
                NotifyTargetMissing();
                return;
            }
            if (Directory.Exists(target.PartitionPath)
                && !TryShowNativeProperties(target.PartitionPath))
            {
                ViewModel.NotificationService.Publish(
                    GlobalNotificationSeverity.Error,
                    Text("无法打开属性", "Could not open properties"),
                    $"{OperationTarget()}{Environment.NewLine}{Text("Windows 未能打开所选分区的属性。请确认该分区仍可用后重试。", "Windows could not open properties for the selected partition. Confirm that it is still available, then try again.")}",
                    "workspace-operation",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = "workspace.properties.native-launch",
                    Code = "workspace.properties.native-launch",
                        SystemId = ViewModel.SelectedSystem.Id,
                        Target = OperationTarget(),
                        Detail = target.PartitionPath
                    });
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
                    ViewModel.NotificationService.Publish(
                        GlobalNotificationSeverity.Warning,
                        Text("属性请求提示", "Properties request notice"),
                        $"{OperationTarget()}{Environment.NewLine}{Text("目标返回了属性请求提示。详细内容可在通知历史中复制。", "The target returned a properties-request notice. Its details can be copied from notification history.")}",
                        "workspace-operation",
                        new GlobalNotificationOptions
                        {
                            OccurrenceKey = $"workspace.properties:{message.Code}",
                            Code = message.Code,
                            SystemId = ViewModel.SelectedSystem.Id,
                            Target = OperationTarget(),
                            Detail = message.DiagnosticText
                        });
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

    private const double MinTopologyWidth = 320;

    private const double TopologyWidthMargin = 20;

    private void TopologyScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(MinTopologyWidth, e.NewSize.Width - TopologyWidthMargin);
        TopologySystemsControl.Width = width;
        foreach (var root in ViewModel.TopologySystems)
        {
            root.SetSurfaceViewportWidth(width);
        }
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
