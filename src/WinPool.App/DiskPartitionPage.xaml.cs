using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationKind = WinPool.Application.SimulationEditKind;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;

namespace WinPool_App;

/// <summary>
/// Disk partition editor (V0.47): the former Edit upper half. Shows the
/// disk/partition topology and keeps disk actions separate from partition
/// actions.
/// </summary>
public sealed partial class DiskPartitionPage : EditorPageBase
{
    private string? _selectedDiskId;
    private string? _selectedPartitionId;
    private long? _selectedUnallocatedOffset;
    private long? _selectedUnallocatedSize;
    private TopologyEditInteraction _interaction = null!;
    private double _viewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;

    public DiskPartitionPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is EditorNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            _selectedDiskId = ResolveOsDiskId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.ActiveSnapshot;
        _interaction = new TopologyEditInteraction(IsTopologyNodeSelected, OnTopologySelected, false);
        LocalizeChrome();
        RefreshAll();
    }

    private void LocalizeChrome()
    {
        ExtendButton.Content = ViewModel.Localization["ExtendVolume"];
        ShrinkButton.Content = ViewModel.Localization["ShrinkVolume"];
        DeletePartitionButton.Content = ViewModel.Localization["DeleteVolume"];
        FormatButton.Content = ViewModel.Localization["Format"];
        NewPartitionButton.Content = ViewModel.Localization["NewPartition"];
        InitializeButton.Content = ViewModel.Localization["InitializeDisk"];
        OfflineButton.Content = Text("脱机 / 联机", "Offline / Online");
        DiskActionsTitle.Text = ViewModel.Localization["DiskActionsSection"];
        PartitionActionsTitle.Text = ViewModel.Localization["PartitionActionsSection"];
    }

    private string? ResolveOsDiskId(string? stableId)
    {
        if (stableId is null)
        {
            return null;
        }

        var snapshot = ViewModel.ActiveSnapshot;
        if (snapshot.OsDisks.Any(x => x.StableId == stableId))
        {
            return stableId;
        }

        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == stableId);
        return partition?.OsDiskStableId
            ?? snapshot.OsDisks.FirstOrDefault(x =>
                x.PhysicalDiskStableId == stableId || x.VirtualDiskStableId == stableId)?.StableId;
    }

    private void RefreshAll()
    {
        RefreshTopology();
        UpdateButtonState();
    }

    private bool IsTopologyNodeSelected(TopologyNodeViewModel node) =>
        node.Unit.StableId == _selectedDiskId
        || node.Unit.StableId == _selectedPartitionId
        || (EditWorkspace.IsUnallocated(node.Unit.StableId)
            && EditWorkspace.TryParseUnallocated(node.Unit.StableId, out var disk, out var offset, out _)
            && disk == _selectedDiskId
            && offset == _selectedUnallocatedOffset);

    private void OnTopologySelected(TopologyNodeViewModel node)
    {
        if (node.Unit.Kind == StorageUnitKind.OsDisk)
        {
            _selectedDiskId = node.Unit.StableId;
            _selectedPartitionId = null;
            _selectedUnallocatedOffset = null;
            _selectedUnallocatedSize = null;
        }
        else if (EditWorkspace.IsUnallocated(node.Unit.StableId)
                 && EditWorkspace.TryParseUnallocated(node.Unit.StableId, out var disk, out var offset, out var size))
        {
            _selectedDiskId = disk;
            _selectedPartitionId = null;
            _selectedUnallocatedOffset = offset;
            _selectedUnallocatedSize = size;
        }
        else if (node.Unit.Kind == StorageUnitKind.Partition)
        {
            _selectedPartitionId = node.Unit.StableId;
            _selectedUnallocatedOffset = null;
            _selectedUnallocatedSize = null;
            var partition = _working.Partitions.FirstOrDefault(item => item.StableId == node.Unit.StableId);
            _selectedDiskId = partition?.OsDiskStableId ?? node.Unit.ParentStableId;
        }

        RefreshTopology();
        UpdateButtonState();
    }

    private void TopologyScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(MinTopologyWidth, e.NewSize.Width - TopologyWidthMargin);
        TopologyControl.Width = width;
        _viewportWidth = width;
        if (TopologyControl.ItemsSource is IReadOnlyList<TopologyNodeViewModel> roots
            && roots.Count > 0)
        {
            roots[0].SetSurfaceViewportWidth(width);
        }
    }

    private void RefreshTopology()
    {
        var root = EditWorkspace.ProjectPartitionWorkspaceRoot(_working, UnallocatedIgnoreBytes);
        var rootViewModel = new TopologyNodeViewModel(
            EditWorkspace.ToManageView(root, ViewModel.ActiveDocument.SystemId, EditWorkspace.PartitionRowStableId),
            ViewModel,
            _working,
            _interaction,
            isLayoutRoot: true);
        rootViewModel.SetSurfaceViewportWidth(_viewportWidth);
        TopologyControl.ItemsSource = new[] { rootViewModel };
        var selected = _working.Partitions.FirstOrDefault(item => item.StableId == _selectedPartitionId);
        SelectedPartitionInfo.Text = selected is null
            ? _selectedUnallocatedOffset is null
                ? string.Empty
                : $"{ViewModel.Localization["Unallocated"]} · {TopologyProjector.FormatBytes(_selectedUnallocatedSize ?? 0)}"
            : $"{ViewModel.PartitionTypeName(selected.Type)} · {(string.IsNullOrWhiteSpace(selected.FileSystem) ? "RAW" : selected.FileSystem)} · {TopologyProjector.FormatBytes(selected.Size)}";
    }

    private void UpdateButtonState()
    {
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var partition = _working.Partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        var disk = _working.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        var isPartitionSelection = partition is not null;
        var isGapSelection = _selectedUnallocatedOffset is not null;
        var isDiskSelection = disk is not null && !isPartitionSelection && !isGapSelection;
        var primary = partition?.Type == "Primary" && partition is { IsBoot: false, IsSystem: false };

        // Disk group: only a selected disk node enables disk actions.
        var diskEditable = simulated && disk is { IsBoot: false, IsSystem: false };
        InitializeButton.IsEnabled = isDiskSelection && diskEditable;
        OfflineButton.IsEnabled = isDiskSelection && simulated && disk is not null;

        // Partition group: only a selected partition or unallocated gap
        // enables partition actions.
        ExtendButton.IsEnabled = isPartitionSelection && simulated && primary == true;
        ShrinkButton.IsEnabled = isPartitionSelection && simulated && primary == true;
        DeletePartitionButton.IsEnabled = isPartitionSelection && simulated && primary == true;
        FormatButton.IsEnabled = isPartitionSelection && simulated && primary == true;
        NewPartitionButton.IsEnabled = simulated
            && disk is { IsOffline: false }
            && (isGapSelection || (isDiskSelection && DiskHasUnallocatedSpace(disk)));
    }

    private bool DiskHasUnallocatedSpace(OsDiskInfo disk)
    {
        var used = _working.Partitions
            .Where(p => p.OsDiskStableId == disk.StableId)
            .Sum(p => p.Size);
        return disk.Size > used;
    }

    private async void Extend_Click(object sender, RoutedEventArgs e) => await ResizeAsync(extend: true);

    private async void Shrink_Click(object sender, RoutedEventArgs e) => await ResizeAsync(extend: false);

    private async Task ResizeAsync(bool extend)
    {
        var partition = _working.Partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        if (partition is null)
        {
            return;
        }

        var title = extend
            ? Text("扩展卷（新大小 GB）", "Extend volume (new size in GB)")
            : Text("压缩卷（新大小 GB）", "Shrink volume (new size in GB)");
        var input = await PromptAsync(title, $"{partition.Size / 1024 / 1024 / 1024}");
        if (input is null || !double.TryParse(input, out var gb) || gb <= 0)
        {
            return;
        }

        await ApplyAsync(new SimulationOperationRequest(
            extend ? SimulationOperationKind.ExtendPartition : SimulationOperationKind.ShrinkPartition,
            partition.StableId,
            SizeBytes: (long)(gb * 1024 * 1024 * 1024)));
        _working = ViewModel.ActiveSnapshot;
        RefreshAll();
    }

    private async void DeletePartition_Click(object sender, RoutedEventArgs e)
    {
        var partition = _working.Partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        if (partition is null || !await ConfirmAsync(
                Text("删除模拟分区", "Delete simulated partition"),
                Text("确定从模拟系统中删除这个分区？", "Remove this partition from the simulation?")))
        {
            return;
        }

        _selectedPartitionId = null;
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.DeletePartition,
            partition.StableId));
        _working = ViewModel.ActiveSnapshot;
        RefreshAll();
    }

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        var partition = _working.Partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        if (partition is null)
        {
            return;
        }

        var osDisk = _working.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
        var isPlainPhysicalDisk = osDisk is { VirtualDiskStableId: null, IsBoot: false, IsSystem: false };
        var primaryCount = _working.Partitions.Count(
            x => x.OsDiskStableId == partition.OsDiskStableId && x.Type == "Primary");
        if (isPlainPhysicalDisk && primaryCount == 1)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Text("推荐初始化磁盘", "Disk initialization recommended"),
                Content = Text(
                    "该分区不是系统盘、不是虚拟磁盘，且磁盘上只有一个主分区。初始化磁盘比格式化更底层，推荐先初始化。",
                    "This partition is the only primary partition on a non-system physical disk. Initializing the disk is lower-level than formatting and is recommended."),
                PrimaryButtonText = Text("跳转到初始化磁盘", "Go to disk initialization"),
                SecondaryButtonText = Text("仅格式化当前分区", "Format this partition only"),
                CloseButtonText = Text("取消", "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary)
            {
                await InitializeAsync();
                return;
            }

            if (choice != ContentDialogResult.Secondary)
            {
                return;
            }
        }

        await FormatAsync(partition);
        _working = ViewModel.ActiveSnapshot;
        RefreshAll();
    }

    private async Task FormatAsync(PartitionInfo partition)
    {
        var fileSystemBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        fileSystemBox.Items.Add("NTFS");
        fileSystemBox.Items.Add("ReFS");
        fileSystemBox.Items.Add("exFAT");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Text("模拟格式化", "Simulated format"),
            Content = fileSystemBox,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.FormatPartition,
            partition.StableId,
            FileSystem: fileSystemBox.SelectedItem as string ?? "NTFS",
            AllocationUnitSize: 65536));
    }

    private async void NewPartition_Click(object sender, RoutedEventArgs e) => await NewPartitionAsync();

    private async Task NewPartitionAsync()
    {
        if (_selectedDiskId is null)
        {
            return;
        }

        var size = await PromptAsync(
            Text("新建分区大小 GB（留空为全部剩余）", "New partition size in GB (blank = all free space)"),
            string.Empty);
        if (size is null)
        {
            return;
        }

        long? bytes = null;
        if (!string.IsNullOrWhiteSpace(size) && TryParseGigabytes(size, out var gb))
        {
            bytes = checked((long)(gb * 1024L * 1024L * 1024L));
        }
        else if (!string.IsNullOrWhiteSpace(size))
        {
            await ShowMessageAsync(
                Text("输入无效", "Invalid input"),
                Text("请输入大于 0 的 GB 数值，或留空使用全部剩余空间。",
                    "Enter a size in GB greater than zero, or leave the field blank to use all free space."));
            return;
        }

        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.CreatePartition,
            _selectedDiskId,
            SizeBytes: bytes ?? _selectedUnallocatedSize,
            OffsetBytes: _selectedUnallocatedOffset));
        _working = ViewModel.ActiveSnapshot;
        RefreshAll();
    }

    private async void Initialize_Click(object sender, RoutedEventArgs e) => await InitializeAsync();

    private async Task InitializeAsync()
    {
        var disk = _working.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        if (disk is null)
        {
            return;
        }

        var styleBox = new ComboBox { SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        styleBox.Items.Add("GPT");
        styleBox.Items.Add("MBR");
        var msrBox = new ToggleSwitch
        {
            IsOn = ViewModel.CurrentPreferences.CreateMsrOnInitialize,
            OnContent = string.Empty,
            OffContent = string.Empty,
            Header = ViewModel.Localization["CreateMsrOnInitialize"]
        };
        var preview = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        void UpdatePreview()
        {
            var lines = new List<string> { "clean", $"convert {(string)styleBox.SelectedItem}".ToLowerInvariant() };
            if (msrBox.IsOn && (string)styleBox.SelectedItem == "GPT")
            {
                lines.Add("create partition msr size=16");
            }

            lines.Add("create partition primary");
            lines.Add("format fs=ntfs quick");
            preview.Text = "DISKPART> " + string.Join("\nDISKPART> ", lines);
        }

        styleBox.SelectionChanged += (_, _) => UpdatePreview();
        msrBox.Toggled += (_, _) => UpdatePreview();
        UpdatePreview();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Text("初始化模拟磁盘", "Initialize simulated disk"),
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 380,
                Children = { styleBox, msrBox, preview }
            },
            PrimaryButtonText = Text("初始化", "Initialize"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _selectedPartitionId = null;
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.InitializeDisk,
            disk.StableId,
            Name: (string)styleBox.SelectedItem,
            CreateMsr: msrBox.IsOn && (string)styleBox.SelectedItem == "GPT"));
        _working = ViewModel.ActiveSnapshot;
        RefreshAll();
    }

    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        var disk = _working.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        if (disk is null)
        {
            return;
        }

        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.SetDiskOffline,
            disk.StableId,
            Offline: !disk.IsOffline));
        _working = ViewModel.ActiveSnapshot;
        RefreshAll();
    }
}
