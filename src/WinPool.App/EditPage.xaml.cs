using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationKind = WinPool.Application.SimulationEditKind;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;
using SimulationOperationResult = WinPool.Application.SimulationEditReceipt;

namespace WinPool_App;

public sealed partial class EditPage : Page
{
    private const string DragFormat = "winpool-physical-disk";
    private WorkspaceViewModel ViewModel { get; set; } = null!;
    private string? _selectedDiskId;
    private string? _selectedPartitionId;
    private readonly List<string> _stagedDiskIds = [];

    public EditPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is EditNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            _selectedDiskId = ResolveOsDiskId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }
        SimulationOnlyInfoBar.Title = Text("仅模拟", "Simulation only");
        SimulationOnlyInfoBar.Message = ViewModel.Localization["EditOnlySimulated"];
        SimulationOnlyInfoBar.IsOpen = !ViewModel.IsUsingSimulatedInventory;
        ResetSimulationButton.Content = ViewModel.Localization["ResetSimulation"];
        ResearchNote.Text = Text(
            "本项目测试结论：64K 交织 + 64K NTFS 簇为当前安全推荐配置。",
            "Tested recommendation: 64K interleave + 64K NTFS cluster.");
        RefreshAll();
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
        if (partition is not null)
        {
            return partition.OsDiskStableId;
        }
        return snapshot.OsDisks.FirstOrDefault(x =>
                x.PhysicalDiskStableId == stableId || x.VirtualDiskStableId == stableId)
            ?.StableId;
    }

    private void RefreshAll()
    {
        RefreshDiskSelector();
        RefreshPartitionBar();
        RefreshPoolTiles();
        UpdateButtonState();
    }

    private void RefreshDiskSelector()
    {
        var snapshot = ViewModel.ActiveSnapshot;
        DiskSelector.Items.Clear();
        foreach (var disk in snapshot.OsDisks.OrderBy(x => x.Number))
        {
            var item = new ComboBoxItem
            {
                Content = $"{Text("磁盘", "Disk")} {disk.Number} - {disk.FriendlyName} ({disk.PartitionStyle}, {TopologyProjector.FormatBytes(disk.Size)})",
                Tag = disk.StableId
            };
            DiskSelector.Items.Add(item);
            if (disk.StableId == _selectedDiskId)
            {
                DiskSelector.SelectedItem = item;
            }
        }
        if (DiskSelector.SelectedItem is null && DiskSelector.Items.Count > 0)
        {
            DiskSelector.SelectedIndex = 0;
        }
        _selectedDiskId = (DiskSelector.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void RefreshPartitionBar()
    {
        PartitionBar.Children.Clear();
        var snapshot = ViewModel.ActiveSnapshot;
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        if (disk is null)
        {
            return;
        }

        var partitions = snapshot.Partitions
            .Where(x => x.OsDiskStableId == disk.StableId)
            .OrderBy(x => x.Offset)
            .ToList();
        var total = Math.Max(1, disk.Size);
        foreach (var partition in partitions)
        {
            var width = Math.Clamp((double)partition.Size / total * 680, 44, 420);
            var label = TopologyProjector.PartitionDisplayName(partition);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = ViewModel.PartitionTypeName(partition.Type);
            }
            var button = new Button
            {
                Width = width,
                Padding = new Thickness(4, 10, 4, 10),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            FontSize = 12,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Text = label
                        },
                        new TextBlock
                        {
                            FontSize = 11,
                            Opacity = 0.75,
                            Text = TopologyProjector.FormatBytes(partition.Size)
                        }
                    }
                },
                Tag = partition.StableId
            };
            if (partition.StableId == _selectedPartitionId)
            {
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }
            button.Click += PartitionSegment_Click;
            PartitionBar.Children.Add(button);
        }

        var used = partitions.Sum(x => x.Size);
        var free = total - used;
        if (free > 32L * 1024 * 1024)
        {
            var width = Math.Clamp((double)free / total * 680, 44, 300);
            var freeButton = new Button
            {
                Width = width,
                Padding = new Thickness(4, 10, 4, 10),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock
                {
                    FontSize = 12,
                    Text = $"{Text("未分配", "Unallocated")} {TopologyProjector.FormatBytes(free)}",
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            freeButton.Click += async (_, _) => await NewPartitionAsync();
            PartitionBar.Children.Add(freeButton);
        }

        var selected = partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        SelectedPartitionInfo.Text = selected is null
            ? string.Empty
            : $"{ViewModel.PartitionTypeName(selected.Type)} · {(string.IsNullOrWhiteSpace(selected.FileSystem) ? "RAW" : selected.FileSystem)} · {TopologyProjector.FormatBytes(selected.Size)} · {Text("可用", "Free")} {TopologyProjector.FormatBytes(selected.SizeRemaining)}";
    }

    private void RefreshPoolTiles()
    {
        PoolTilesPanel.Children.Clear();
        StagedDisksPanel.Children.Clear();
        var snapshot = ViewModel.ActiveSnapshot;

        foreach (var pool in snapshot.StoragePools.Where(x => !x.IsPrimordial))
        {
            var members = snapshot.PhysicalDisks
                .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var tile = new Border
            {
                Padding = new Thickness(12),
                AllowDrop = true,
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Tag = pool.StableId
            };
            tile.DragOver += PoolTile_DragOver;
            tile.Drop += PoolTile_Drop;
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Text = $"{pool.FriendlyName} · {TopologyProjector.FormatBytes(pool.Size)}"
            });
            foreach (var member in members)
            {
                stack.Children.Add(new TextBlock
                {
                    FontSize = 12,
                    Text = $"• {member.FriendlyName} ({TopologyProjector.FormatBytes(member.Size)})"
                });
            }
            tile.Child = stack;
            PoolTilesPanel.Children.Add(tile);
        }

        foreach (var id in _stagedDiskIds.ToArray())
        {
            var disk = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == id);
            if (disk is null)
            {
                _stagedDiskIds.Remove(id);
                continue;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Text = $"• {disk.FriendlyName} ({TopologyProjector.FormatBytes(disk.Size)})"
            });
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), Tag = id };
            remove.Click += StagedDiskRemove_Click;
            row.Children.Add(remove);
            StagedDisksPanel.Children.Add(row);
        }

        var primordial = snapshot.StoragePools.FirstOrDefault(x => x.IsPrimordial);
        var available = primordial is null
            ? []
            : snapshot.PhysicalDisks
                .Where(x => x.PoolStableId == primordial.StableId
                            && !_stagedDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                .Select(x => $"{x.FriendlyName} ({TopologyProjector.FormatBytes(x.Size)})|{x.StableId}")
                .ToArray();
        PrimordialDiskList.ItemsSource = available
            .Select(x => x[..x.LastIndexOf('|')]).ToArray();
        PrimordialDiskList.Tag = available;
    }

    private void UpdateButtonState()
    {
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var snapshot = ViewModel.ActiveSnapshot;
        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        var primary = partition?.Type == "Primary" && partition is { IsBoot: false, IsSystem: false };
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        var diskEditable = simulated && disk is { IsBoot: false, IsSystem: false };

        ExtendButton.IsEnabled = simulated && primary == true;
        ShrinkButton.IsEnabled = simulated && primary == true;
        DeletePartitionButton.IsEnabled = simulated && primary == true;
        FormatButton.IsEnabled = simulated && primary == true;
        NewPartitionButton.IsEnabled = simulated && disk is { IsOffline: false };
        InitializeButton.IsEnabled = diskEditable;
        OfflineButton.IsEnabled = simulated && disk is not null;
        CreatePoolButton.IsEnabled = simulated && _stagedDiskIds.Count > 0;
        ResetSimulationButton.IsEnabled = ViewModel.SelectedSystem.Id.StartsWith(
            "simulation:builtin", StringComparison.Ordinal);
    }

    private void DiskSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDiskId = (DiskSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        _selectedPartitionId = null;
        RefreshPartitionBar();
        UpdateButtonState();
    }

    private void PartitionSegment_Click(object sender, RoutedEventArgs e)
    {
        _selectedPartitionId = ((FrameworkElement)sender).Tag as string;
        RefreshPartitionBar();
        UpdateButtonState();
    }

    private async void Extend_Click(object sender, RoutedEventArgs e) => await ResizeAsync(extend: true);

    private async void Shrink_Click(object sender, RoutedEventArgs e) => await ResizeAsync(extend: false);

    private async Task ResizeAsync(bool extend)
    {
        var partition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(
            x => x.StableId == _selectedPartitionId);
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
    }

    private async void DeletePartition_Click(object sender, RoutedEventArgs e)
    {
        var partition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(
            x => x.StableId == _selectedPartitionId);
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
    }

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        var partition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(
            x => x.StableId == _selectedPartitionId);
        if (partition is null)
        {
            return;
        }

        var snapshot = ViewModel.ActiveSnapshot;
        var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
        var isPlainPhysicalDisk = osDisk is { VirtualDiskStableId: null, IsBoot: false, IsSystem: false };
        var primaryCount = snapshot.Partitions.Count(
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
    }

    private async Task FormatAsync(PartitionInfo partition)
    {
        var fileSystem = await PromptAsync(
            Text("模拟格式化：NTFS / ReFS / exFAT", "Simulated format: NTFS / ReFS / exFAT"),
            "NTFS");
        if (fileSystem is null)
        {
            return;
        }
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.FormatPartition,
            partition.StableId,
            FileSystem: fileSystem,
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
        if (!string.IsNullOrWhiteSpace(size)
            && double.TryParse(size, out var gb)
            && gb > 0)
        {
            bytes = (long)(gb * 1024 * 1024 * 1024);
        }
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.CreatePartition,
            _selectedDiskId,
            SizeBytes: bytes));
    }

    private async void Initialize_Click(object sender, RoutedEventArgs e) => await InitializeAsync();

    private async Task InitializeAsync()
    {
        var disk = ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        if (disk is null)
        {
            return;
        }

        var styleBox = new ComboBox { SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        styleBox.Items.Add("GPT");
        styleBox.Items.Add("MBR");
        var msrBox = new CheckBox
        {
            IsChecked = ViewModel.CurrentPreferences.CreateMsrOnInitialize,
            Content = ViewModel.Localization["CreateMsrOnInitialize"]
        };
        var preview = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        void UpdatePreview()
        {
            var lines = new List<string> { "clean", $"convert {(string)styleBox.SelectedItem}".ToLowerInvariant() };
            if (msrBox.IsChecked == true && (string)styleBox.SelectedItem == "GPT")
            {
                lines.Add("create partition msr size=16");
            }
            lines.Add("create partition primary");
            lines.Add("format fs=ntfs quick");
            preview.Text = "DISKPART> " + string.Join("\nDISKPART> ", lines);
        }
        styleBox.SelectionChanged += (_, _) => UpdatePreview();
        msrBox.Click += (_, _) => UpdatePreview();
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
            CreateMsr: msrBox.IsChecked == true && (string)styleBox.SelectedItem == "GPT"));
    }

    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        var disk = ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        if (disk is null)
        {
            return;
        }
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.SetDiskOffline,
            disk.StableId,
            Offline: !disk.IsOffline));
    }

    private void StagedDiskRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id)
        {
            _stagedDiskIds.Remove(id);
        }
        RefreshPoolTiles();
        UpdateButtonState();
    }

    private void PrimordialDiskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (PrimordialDiskList.Tag is string[] entries
            && e.Items.FirstOrDefault() is string display)
        {
            var entry = entries.FirstOrDefault(x => x.StartsWith(display, StringComparison.Ordinal));
            var id = entry?[(entry.LastIndexOf('|') + 1)..];
            if (!string.IsNullOrWhiteSpace(id))
            {
                e.Data.SetText(id);
                e.Data.RequestedOperation = DataPackageOperation.Move;
            }
        }
    }

    private void PoolTile_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = Text("移动磁盘", "Move disk");
        }
    }

    private async void PoolTile_Drop(object sender, DragEventArgs e)
    {
        var diskId = await e.DataView.GetTextAsync();
        var poolId = ((FrameworkElement)sender).Tag as string;
        if (string.IsNullOrWhiteSpace(diskId) || string.IsNullOrWhiteSpace(poolId))
        {
            return;
        }
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.MovePhysicalDisk,
            diskId,
            Name: poolId));
    }

    private void NewPoolTile_Drop(object sender, DragEventArgs e)
    {
        _ = StageDiskAsync(e);
    }

    private async Task StageDiskAsync(DragEventArgs e)
    {
        var diskId = await e.DataView.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(diskId)
            && !_stagedDiskIds.Contains(diskId, StringComparer.OrdinalIgnoreCase))
        {
            _stagedDiskIds.Add(diskId);
        }
        RefreshPoolTiles();
        UpdateButtonState();
    }

    private void PrimordialTile_Drop(object sender, DragEventArgs e)
    {
        _ = UnstageOrMoveToPrimordialAsync(e);
    }

    private async Task UnstageOrMoveToPrimordialAsync(DragEventArgs e)
    {
        var diskId = await e.DataView.GetTextAsync();
        if (string.IsNullOrWhiteSpace(diskId))
        {
            return;
        }
        if (_stagedDiskIds.Remove(diskId))
        {
            RefreshPoolTiles();
            UpdateButtonState();
            return;
        }
        await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.MovePhysicalDisk,
            diskId,
            Name: string.Empty));
    }

    private async void CreatePool_Click(object sender, RoutedEventArgs e)
    {
        if (_stagedDiskIds.Count == 0)
        {
            return;
        }

        var poolName = string.IsNullOrWhiteSpace(PoolNameBox.Text) ? "Pool" : PoolNameBox.Text.Trim();
        var virtualName = string.IsNullOrWhiteSpace(VirtualDiskNameBox.Text)
            ? poolName
            : VirtualDiskNameBox.Text.Trim();
        var interleave = ParseSize((string)(InterleaveBox.SelectedItem as string ?? "64K"));
        var cluster = ParseSize((string)(ClusterSizeBox.SelectedItem as string ?? "64K"));
        long? vdiskSize = null;
        if (!string.IsNullOrWhiteSpace(VirtualDiskSizeBox.Text)
            && double.TryParse(VirtualDiskSizeBox.Text, out var gb)
            && gb > 0)
        {
            vdiskSize = (long)(gb * 1024 * 1024 * 1024);
        }

        var result = await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.CreateStoragePool,
            "primordial",
            Name: poolName,
            MemberDiskIds: _stagedDiskIds.ToArray()));
        if (result is null)
        {
            return;
        }

        var pool = ViewModel.ActiveSnapshot.StoragePools.FirstOrDefault(
            x => !x.IsPrimordial && x.FriendlyName == poolName);
        if (pool is null)
        {
            return;
        }

        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.CreateVirtualDisk,
                pool.StableId,
                Name: virtualName,
                Resiliency: (string)(ResiliencyBox.SelectedItem as string ?? "Simple"),
                InterleaveBytes: interleave,
                SizeBytes: vdiskSize,
                AllocationUnitSize: cluster)) is null)
        {
            return;
        }

        var osDisk = ViewModel.ActiveSnapshot.OsDisks.FirstOrDefault(
            x => x.VirtualDiskStableId is not null
                 && ViewModel.ActiveSnapshot.VirtualDisks.Any(v =>
                     v.StableId == x.VirtualDiskStableId && v.FriendlyName == virtualName));
        if (osDisk is null)
        {
            return;
        }

        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.CreatePartition,
                osDisk.StableId)) is null)
        {
            return;
        }

        var partition = ViewModel.ActiveSnapshot.Partitions
            .Where(x => x.OsDiskStableId == osDisk.StableId)
            .OrderByDescending(x => x.Offset)
            .FirstOrDefault();
        if (partition is not null)
        {
            await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.FormatPartition,
                partition.StableId,
                Name: virtualName,
                FileSystem: "NTFS",
                AllocationUnitSize: cluster));
        }

        _stagedDiskIds.Clear();
        RefreshAll();
    }

    private async void ResetSimulation_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                Text("重置模拟数据", "Reset simulation data"),
                Text("恢复当前模拟系统到初始状态？全部模拟修改都会丢失。",
                     "Restore the current simulation to its initial state? All simulated changes are lost.")))
        {
            return;
        }
        await ViewModel.ResetActiveSimulationAsync();
        _selectedPartitionId = null;
        _stagedDiskIds.Clear();
        RefreshAll();
    }

    private static long ParseSize(string token) =>
        token.TrimEnd('K', 'k') is var digits && long.TryParse(digits, out var value)
            ? value * 1024
            : 65536;

    private async Task<SimulationOperationResult?> ApplyAsync(SimulationOperationRequest request)
    {
        var result = await ViewModel.ApplySimulationOperationAsync(request);
        if (!result.IsSuccess || result.Value is null)
        {
            await ShowMessageAsync(
                Text("操作不可用", "Operation unavailable"),
                result.Messages.FirstOrDefault()?.UserTextKey
                    ?? Text("模拟操作未完成。", "The simulation operation did not complete."));
            return null;
        }
        RefreshAll();
        return result.Value;
    }

    private async Task<string?> PromptAsync(string title, string value)
    {
        var input = new TextBox { Text = value, MinWidth = 320 };
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
        ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? zh : en;
}
