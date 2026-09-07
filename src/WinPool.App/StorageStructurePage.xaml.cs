using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationKind = WinPool.Application.SimulationEditKind;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;

namespace WinPool_App;

/// <summary>
/// Storage structure editor (V0.47): the former Edit lower half. Shows the
/// pool topology with a working draft, structure operations, and pool /
/// virtual-disk / partition properties.
/// </summary>
public sealed partial class StorageStructurePage : EditorPageBase
{
    private string? _selectedPoolId;
    private string? _selectedPoolDiskId;
    private string? _selectedPoolVdiskId;
    private TopologyEditInteraction _interaction = null!;
    private bool _formBuilt;
    private double _viewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;

    private readonly CheckBox _autoPartitionBox = new() { IsChecked = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<FrameworkElement> _autoPartitionRows = [];
    private readonly TextBox _poolNameBox = new();
    private readonly TextBox _virtualDiskNameBox = new();
    private readonly ComboBox _performanceResiliencyBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _performanceInterleaveBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _performanceSizeBox = new();
    private readonly TextBox _performanceColumnsBox = new() { IsReadOnly = true };
    private readonly TextBox _performanceCopiesBox = new();
    private readonly TextBox _performanceFailuresBox = new() { IsReadOnly = true };
    private readonly ComboBox _capacityResiliencyBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _capacityInterleaveBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _capacitySizeBox = new();
    private readonly TextBox _capacityColumnsBox = new();
    private readonly TextBox _capacityCopiesBox = new() { IsReadOnly = true };
    private readonly TextBox _capacityFailuresBox = new();
    private readonly ComboBox _scmResiliencyBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _scmInterleaveBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _scmSizeBox = new();
    private readonly TextBox _scmColumnsBox = new() { IsReadOnly = true };
    private readonly TextBox _scmCopiesBox = new();
    private readonly TextBox _scmFailuresBox = new() { IsReadOnly = true };
    private readonly ComboBox _fileSystemBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _clusterBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _researchNote = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
    };
    private readonly TextBlock _multiVdiskWarning = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
    };
    private readonly List<FrameworkElement> _scmRows = [];

    public StorageStructurePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is EditorNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            _selectedPoolId = ResolvePoolId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.ActiveSnapshot;
        _interaction = new TopologyEditInteraction(
            IsTopologyNodeSelected,
            OnTopologySelected,
            ViewModel.IsUsingSimulatedInventory,
            OnDiskDropped);
        LocalizeChrome();
        EnsureForm();
        RefreshAll();
    }

    private void LocalizeChrome()
    {
        StructureOperationsTitle.Text = ViewModel.Localization["StructureOperationsSection"];
        _researchNote.Text = ViewModel.Localization["ResearchNote64k"];
        _multiVdiskWarning.Text = ViewModel.Localization["MultipleVirtualDiskWarning"];
        _performanceSizeBox.PlaceholderText = ViewModel.Localization["SizeGbPlaceholder"];
        _capacitySizeBox.PlaceholderText = ViewModel.Localization["SizeGbPlaceholder"];
        _scmSizeBox.PlaceholderText = ViewModel.Localization["SizeGbPlaceholder"];
    }

    private void EnsureForm()
    {
        if (_formBuilt)
        {
            return;
        }

        _formBuilt = true;
        FillCombo(_performanceResiliencyBox, ["Simple", "Mirror", "Parity"], 1);
        FillCombo(_capacityResiliencyBox, ["Simple", "Mirror", "Parity"], 2);
        FillCombo(_scmResiliencyBox, ["Simple", "Mirror", "Parity"], 1);
        FillCombo(_performanceInterleaveBox, ["32K", "64K", "128K", "256K"], 1);
        FillCombo(_capacityInterleaveBox, ["32K", "64K", "128K", "256K"], 1);
        FillCombo(_scmInterleaveBox, ["32K", "64K", "128K", "256K"], 1);
        FillCombo(_fileSystemBox, ["NTFS", "ReFS", "exFAT"], 0);
        FillCombo(_clusterBox, ["4K", "8K", "16K", "32K", "64K"], 4);
        _performanceResiliencyBox.SelectionChanged += (_, _) => UpdateLinkedFields();
        _capacityResiliencyBox.SelectionChanged += (_, _) => UpdateLinkedFields();
        _scmResiliencyBox.SelectionChanged += (_, _) => UpdateLinkedFields();
        _performanceCopiesBox.LostFocus += (_, _) => UpdateLinkedFields();
        _capacityFailuresBox.LostFocus += (_, _) => UpdateLinkedFields();
        _scmCopiesBox.LostFocus += (_, _) => UpdateLinkedFields();

        var row = 0;
        AddSectionHeader(row++, "PoolPropertiesSection");
        AddFormRow(row++, "PoolName", _poolNameBox);
        AddFormRow(row++, "VirtualDiskName", _virtualDiskNameBox);
        AddAutoPartitionRow(row++, "AutoCreatePartition", _autoPartitionBox);
        AddFormRow(row++, "PerformanceResiliency", _performanceResiliencyBox);
        AddFormRow(row++, "PerformanceInterleave", _performanceInterleaveBox);
        AddFormRow(row++, "PerformanceSize", _performanceSizeBox);
        AddFormRow(row++, "PerformanceColumns", _performanceColumnsBox);
        AddFormRow(row++, "PerformanceCopies", _performanceCopiesBox);
        AddFormRow(row++, "PerformanceFailures", _performanceFailuresBox);
        AddScmRow(row++, "ScmResiliency", _scmResiliencyBox);
        AddScmRow(row++, "ScmInterleave", _scmInterleaveBox);
        AddScmRow(row++, "ScmSize", _scmSizeBox);
        AddScmRow(row++, "ScmColumns", _scmColumnsBox);
        AddScmRow(row++, "ScmCopies", _scmCopiesBox);
        AddScmRow(row++, "ScmFailures", _scmFailuresBox);
        AddFormRow(row++, "CapacityResiliency", _capacityResiliencyBox);
        AddFormRow(row++, "CapacityInterleave", _capacityInterleaveBox);
        AddFormRow(row++, "CapacitySize", _capacitySizeBox);
        AddFormRow(row++, "CapacityColumns", _capacityColumnsBox);
        AddFormRow(row++, "CapacityCopies", _capacityCopiesBox);
        AddFormRow(row++, "CapacityFailures", _capacityFailuresBox);
        AddFormRow(row++, "PartitionFileSystem", _fileSystemBox);
        AddFormRow(row++, "PartitionClusterSize", _clusterBox);
        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_researchNote, row);
        Grid.SetColumn(_researchNote, 0);
        Grid.SetColumnSpan(_researchNote, 2);
        PoolFormGrid.Children.Add(_researchNote);
        row++;
        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_multiVdiskWarning, row);
        Grid.SetColumn(_multiVdiskWarning, 0);
        Grid.SetColumnSpan(_multiVdiskWarning, 2);
        PoolFormGrid.Children.Add(_multiVdiskWarning);
    }

    private void AddScmRow(int row, string key, FrameworkElement value)
    {
        var label = AddFormRow(row, key, value);
        _scmRows.Add(label);
        _scmRows.Add(value);
    }

    private void AddAutoPartitionRow(int row, string key, FrameworkElement value)
    {
        var label = AddFormRow(row, key, value);
        _autoPartitionRows.Add(label);
        _autoPartitionRows.Add(value);
    }

    private void AddSectionHeader(int row, string key)
    {
        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 4),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = ViewModel.Localization[key]
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetColumnSpan(label, 2);
        PoolFormGrid.Children.Add(label);
    }

    private TextBlock AddFormRow(int row, string key, FrameworkElement value)
    {
        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = string.IsNullOrEmpty(key) ? string.Empty : ViewModel.Localization[key]
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        PoolFormGrid.Children.Add(label);
        PoolFormGrid.Children.Add(value);
        return label;
    }

    private static void FillCombo(ComboBox box, IReadOnlyList<string> items, int selected)
    {
        box.Items.Clear();
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        box.SelectedIndex = selected;
    }

    private string? ResolvePoolId(string? stableId)
    {
        if (stableId is null)
        {
            return null;
        }

        var snapshot = ViewModel.ActiveSnapshot;
        if (snapshot.StoragePools.Any(item => item.StableId == stableId))
        {
            return stableId;
        }

        return snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == stableId)?.PoolStableId
            ?? snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == stableId)?.PoolStableId;
    }

    private void RefreshAll()
    {
        RefreshTopology();
        FillPoolForm();
        UpdateButtonState();
    }

    private bool IsTopologyNodeSelected(TopologyNodeViewModel node) =>
        node.Unit.Kind switch
        {
            StorageUnitKind.PhysicalDisk => node.Unit.StableId == _selectedPoolDiskId,
            StorageUnitKind.VirtualDisk => node.Unit.StableId == _selectedPoolVdiskId,
            StorageUnitKind.StoragePool => node.Unit.StableId == _selectedPoolId
                && string.IsNullOrEmpty(_selectedPoolDiskId)
                && string.IsNullOrEmpty(_selectedPoolVdiskId),
            _ => false
        };

    private void OnTopologySelected(TopologyNodeViewModel node)
    {
        if (EditWorkspace.IsPlus(node.Unit.StableId))
        {
            var existingDraft = _working.StoragePools.LastOrDefault(item => EditWorkspace.IsDraftPool(item.StableId));
            if (existingDraft is not null)
            {
                _selectedPoolId = existingDraft.StableId;
                _selectedPoolDiskId = null;
                _selectedPoolVdiskId = null;
                RefreshTopology();
                FillPoolForm();
                UpdateButtonState();
                return;
            }

            _working = EditWorkspace.InsertDraftPool(_working, NextPoolName());
            _selectedPoolId = _working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
            _selectedPoolDiskId = null;
            _selectedPoolVdiskId = null;
            RefreshTopology();
            FillPoolForm();
            UpdateButtonState();
            return;
        }

        switch (node.Unit.Kind)
        {
            case StorageUnitKind.PhysicalDisk:
                _selectedPoolDiskId = node.Unit.StableId;
                _selectedPoolVdiskId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.PhysicalDisks.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            case StorageUnitKind.VirtualDisk:
                _selectedPoolVdiskId = node.Unit.StableId;
                _selectedPoolDiskId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.VirtualDisks.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            default:
                _selectedPoolVdiskId = null;
                _selectedPoolDiskId = null;
                _selectedPoolId = node.Unit.Kind == StorageUnitKind.StoragePool
                    ? node.Unit.StableId
                    : node.Unit.ParentStableId;
                break;
        }

        RefreshTopology();
        FillPoolForm();
        UpdateButtonState();
    }

    private void OnDiskDropped(string diskId, string poolId)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        // Drag-out rule (Plan §7.3, refined): a real pool whose structure
        // modification is unsupported freezes its ORIGINAL committed
        // members. A disk moved in during this session can always be
        // dragged back out, otherwise the move-in would deadlock.
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        var sourcePoolId = workingDisk?.PoolStableId;
        if (!string.IsNullOrEmpty(sourcePoolId)
            && !EditWorkspace.IsDraftPool(sourcePoolId)
            && _working.StoragePools.FirstOrDefault(candidate =>
                candidate.StableId == sourcePoolId) is { IsPrimordial: false } sourcePoolInfo
            && !EditWorkspace.PoolSupportsStructureModification(_working, sourcePoolInfo.StableId))
        {
            var committedDisk = ViewModel.ActiveSnapshot.PhysicalDisks.FirstOrDefault(
                item => item.StableId == diskId);
            var wasOriginalMember = committedDisk is not null
                && string.Equals(
                    committedDisk.PoolStableId,
                    sourcePoolId,
                    StringComparison.OrdinalIgnoreCase);
            if (wasOriginalMember && EditWorkspace.DiskIsAssignedToTier(_working, diskId))
            {
                return;
            }
        }

        var selected = SelectedPool();
        if (selected is not null && EditWorkspace.HasMultipleVirtualDisks(_working, selected.StableId))
        {
            _ = ShowMessageAsync(ViewModel.Localization["Warning"], ViewModel.Localization["MultipleVirtualDiskWarning"]);
            return;
        }

        try
        {
            if (EditWorkspace.IsPlus(poolId))
            {
                var existingDraft = _working.StoragePools.LastOrDefault(item => EditWorkspace.IsDraftPool(item.StableId));
                if (existingDraft is null)
                {
                    _working = EditWorkspace.InsertDraftPool(_working, NextPoolName());
                    existingDraft = _working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId));
                }

                poolId = existingDraft.StableId;
                _selectedPoolId = poolId;
            }

            _working = EditWorkspace.MoveDiskToPool(_working, diskId, poolId);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
            return;
        }

        RefreshTopology();
        FillPoolForm();
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
        var root = EditWorkspace.ProjectPoolWorkspaceRoot(
            _working,
            UnallocatedIgnoreBytes,
            ViewModel.ActiveSnapshot);
        var rootViewModel = new TopologyNodeViewModel(
            EditWorkspace.ToManageView(root, ViewModel.ActiveDocument.SystemId, "edit-pool-row"),
            ViewModel,
            _working,
            _interaction,
            isLayoutRoot: true);
        rootViewModel.SetSurfaceViewportWidth(_viewportWidth);
        ApplyPendingModificationStates(rootViewModel);
        TopologyControl.ItemsSource = new[] { rootViewModel };
    }

    private void ApplyPendingModificationStates(TopologyNodeViewModel root)
    {
        var committed = ViewModel.ActiveSnapshot;
        var queue = new Queue<TopologyNodeViewModel>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.ShowsEditStatus)
            {
                node.SetPendingModifications(
                    EditWorkspace.HasPendingModifications(_working, committed, node.Unit.StableId));
            }

            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    private StoragePoolInfo? SelectedPool() =>
        _working.StoragePools.FirstOrDefault(item => item.StableId == _selectedPoolId);

    private void FillPoolForm()
    {
        PoolFormGrid.Visibility = Visibility.Visible;
        var pool = SelectedPool();
        var showScm = EditWorkspace.HasScmDisk(_working);
        foreach (var element in _scmRows)
        {
            element.Visibility = showScm ? Visibility.Visible : Visibility.Collapsed;
        }

        var isCreateContext = pool is null || EditWorkspace.IsDraftPool(pool.StableId);
        foreach (var element in _autoPartitionRows)
        {
            element.Visibility = isCreateContext ? Visibility.Visible : Visibility.Collapsed;
        }

        if (pool is null || pool.IsPrimordial)
        {
            FillRecommendedDefaults();
            return;
        }

        var ssd = Tier(pool.StableId, "SSD");
        var hdd = Tier(pool.StableId, "HDD");
        var scm = Tier(pool.StableId, "SCM");
        var vdisk = _working.VirtualDisks.FirstOrDefault(item => item.PoolStableId == pool.StableId);
        _poolNameBox.Text = pool.FriendlyName;
        _virtualDiskNameBox.Text = vdisk?.FriendlyName ?? pool.FriendlyName;
        SetResiliency(_performanceResiliencyBox, ssd?.ResiliencySettingName ?? "Mirror");
        SetResiliency(_capacityResiliencyBox, hdd?.ResiliencySettingName ?? "Parity");
        SetResiliency(_scmResiliencyBox, scm?.ResiliencySettingName ?? "Mirror");
        SetInterleave(_performanceInterleaveBox, ssd?.Interleave ?? 65536);
        SetInterleave(_capacityInterleaveBox, hdd?.Interleave ?? 65536);
        SetInterleave(_scmInterleaveBox, scm?.Interleave ?? 65536);
        _performanceSizeBox.Text = ToGigabytes(ssd?.Size);
        _capacitySizeBox.Text = ToGigabytes(hdd?.Size);
        _scmSizeBox.Text = ToGigabytes(scm?.Size);
        _performanceColumnsBox.Text = ssd?.NumberOfColumns?.ToString() ?? "auto";
        _capacityColumnsBox.Text = (hdd?.NumberOfColumns ?? EditWorkspace.RecommendedCapacityColumns(
            Members(hdd))).ToString();
        _scmColumnsBox.Text = scm?.NumberOfColumns?.ToString() ?? "auto";
        _performanceCopiesBox.Text = (ssd?.NumberOfDataCopies ?? 2).ToString();
        _capacityCopiesBox.Text = (hdd?.NumberOfDataCopies ?? 1).ToString();
        _scmCopiesBox.Text = (scm?.NumberOfDataCopies ?? 2).ToString();
        _performanceFailuresBox.Text = (ssd?.PhysicalDiskRedundancy ?? 1).ToString();
        _capacityFailuresBox.Text = (hdd?.PhysicalDiskRedundancy ?? 1).ToString();
        _scmFailuresBox.Text = (scm?.PhysicalDiskRedundancy ?? 1).ToString();
        _fileSystemBox.SelectedItem = "NTFS";
        _clusterBox.SelectedItem = "64K";
        var partition = PartitionForPool(pool.StableId);
        if (partition is not null)
        {
            if (!string.IsNullOrWhiteSpace(partition.FileSystem)
                && _fileSystemBox.Items.Contains(partition.FileSystem))
            {
                _fileSystemBox.SelectedItem = partition.FileSystem;
            }

            if (partition.AllocationUnitSize is long cluster)
            {
                _clusterBox.SelectedItem = cluster switch
                {
                    4096 => "4K",
                    8192 => "8K",
                    16384 => "16K",
                    32768 => "32K",
                    _ => "64K"
                };
            }
        }

        UpdateLinkedFields();
    }

    private void FillRecommendedDefaults()
    {
        _poolNameBox.Text = NextPoolName();
        _virtualDiskNameBox.Text = _poolNameBox.Text;
        _autoPartitionBox.IsChecked = true;
        SetResiliency(_performanceResiliencyBox, "Mirror");
        SetResiliency(_capacityResiliencyBox, "Parity");
        SetResiliency(_scmResiliencyBox, "Mirror");
        SetInterleave(_performanceInterleaveBox, 65536);
        SetInterleave(_capacityInterleaveBox, 65536);
        SetInterleave(_scmInterleaveBox, 65536);
        _performanceSizeBox.Text = string.Empty;
        _capacitySizeBox.Text = string.Empty;
        _scmSizeBox.Text = string.Empty;
        _performanceColumnsBox.Text = "auto";
        _capacityColumnsBox.Text = "5";
        _scmColumnsBox.Text = "auto";
        _performanceCopiesBox.Text = "2";
        _capacityCopiesBox.Text = "1";
        _scmCopiesBox.Text = "2";
        _performanceFailuresBox.Text = "1";
        _capacityFailuresBox.Text = "1";
        _scmFailuresBox.Text = "1";
        _fileSystemBox.SelectedItem = "NTFS";
        _clusterBox.SelectedItem = "64K";
        UpdateLinkedFields();
    }

    private StorageTierInfo? Tier(string poolId, string media) =>
        _working.StorageTiers.FirstOrDefault(item =>
            item.PoolStableId == poolId && EditWorkspace.NormalizeMedia(item.MediaType) == media);

    private IReadOnlyList<PhysicalDiskInfo> Members(StorageTierInfo? tier) =>
        tier is null
            ? []
            : _working.PhysicalDisks
                .Where(disk => tier.MemberPhysicalDiskIds.Contains(disk.StableId, StringComparer.OrdinalIgnoreCase))
                .ToArray();

    private PartitionInfo? PartitionForPool(string poolId)
    {
        var vdisk = _working.VirtualDisks.FirstOrDefault(item => item.PoolStableId == poolId);
        var osDisk = _working.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk?.StableId);
        return _working.Partitions
            .Where(item => item.OsDiskStableId == osDisk?.StableId)
            .OrderBy(item => item.Offset)
            .FirstOrDefault();
    }

    private static void SetResiliency(ComboBox box, string value)
    {
        var match = box.Items.OfType<string>().FirstOrDefault(item =>
            item.Equals(value, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items.OfType<string>().First();
    }

    private static void SetInterleave(ComboBox box, long bytes)
    {
        var token = $"{Math.Max(1, bytes / 1024)}K";
        box.SelectedItem = box.Items.OfType<string>().Contains(token) ? token : "64K";
    }

    private static string ToGigabytes(long? bytes) =>
        bytes is > 0 ? Math.Round(bytes.Value / 1024d / 1024d / 1024d, 2).ToString("0.##") : string.Empty;

    private void UpdateLinkedFields()
    {
        LinkResiliency(_performanceResiliencyBox, _performanceCopiesBox, _performanceFailuresBox, copiesMaster: true);
        LinkResiliency(_capacityResiliencyBox, _capacityCopiesBox, _capacityFailuresBox, copiesMaster: false);
        LinkResiliency(_scmResiliencyBox, _scmCopiesBox, _scmFailuresBox, copiesMaster: true);
    }

    private static void LinkResiliency(ComboBox resiliencyBox, TextBox copiesBox, TextBox failuresBox, bool copiesMaster)
    {
        var resiliency = resiliencyBox.SelectedItem as string ?? "Simple";
        if (resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase))
        {
            copiesBox.Text = "1";
            failuresBox.Text = "0";
            copiesBox.IsEnabled = false;
            failuresBox.IsEnabled = false;
            return;
        }

        if (resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            copiesBox.IsEnabled = copiesMaster;
            failuresBox.IsEnabled = false;
            if (!int.TryParse(copiesBox.Text, out var copies) || copies < 2)
            {
                copies = 2;
                copiesBox.Text = "2";
            }

            failuresBox.Text = Math.Max(0, copies - 1).ToString();
            return;
        }

        copiesBox.IsEnabled = false;
        failuresBox.IsEnabled = !copiesMaster;
        copiesBox.Text = "1";
        if (!int.TryParse(failuresBox.Text, out var tolerated) || tolerated < 1)
        {
            failuresBox.Text = "1";
        }
    }

    private void UpdateButtonState()
    {
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var pool = SelectedPool();
        var multi = pool is not null && EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId);
        _multiVdiskWarning.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        var isDraft = pool is not null && EditWorkspace.IsDraftPool(pool.StableId);
        ExecuteButton.Content = isDraft
            ? ViewModel.Localization["CreateNewPool"]
            : ViewModel.Localization["ExecuteModify"];
        DissolveButton.Content = ViewModel.Localization["DissolvePool"];
        EvictButton.Content = ViewModel.Localization["EvictDisk"];
        DeleteVdiskButton.Content = ViewModel.Localization["DeleteVirtualDisk"];
        ExecuteButton.IsEnabled = simulated
            && pool is { IsPrimordial: false }
            && !multi
            && (!isDraft || pool.MemberPhysicalDiskIds.Count > 0);
        DissolveButton.IsEnabled = simulated && pool is { IsPrimordial: false };
        var selectedVdisk = _working.VirtualDisks.FirstOrDefault(item => item.StableId == _selectedPoolVdiskId);
        DeleteVdiskButton.IsEnabled = simulated
            && selectedVdisk is not null
            && pool is { IsPrimordial: false }
            && EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId)
            && string.Equals(
                selectedVdisk.PoolStableId,
                pool.StableId,
                StringComparison.OrdinalIgnoreCase);
        var selectedDisk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == _selectedPoolDiskId);
        EvictButton.IsEnabled = simulated
            && pool is { IsPrimordial: false }
            && selectedDisk is not null
            && string.Equals(selectedDisk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
            && EditWorkspace.DiskIsAssignedToTier(_working, selectedDisk.StableId);
        var formEnabled = simulated && pool is { IsPrimordial: false } && !multi;
        if (_formBuilt)
        {
            var isCreateContext = pool is null || EditWorkspace.IsDraftPool(pool.StableId);
            foreach (var element in _autoPartitionRows)
            {
                element.Visibility = isCreateContext ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var control in new Control[]
                 {
                     _poolNameBox, _virtualDiskNameBox, _performanceResiliencyBox, _performanceInterleaveBox,
                     _performanceSizeBox, _performanceCopiesBox, _capacityResiliencyBox, _capacityInterleaveBox,
                     _capacitySizeBox, _capacityColumnsBox, _capacityFailuresBox, _scmResiliencyBox,
                     _scmInterleaveBox, _scmSizeBox, _scmCopiesBox, _fileSystemBox, _clusterBox
                 })
        {
            control.IsEnabled = formEnabled;
        }

        _performanceColumnsBox.IsEnabled = false;
        _scmColumnsBox.IsEnabled = false;
        _capacityCopiesBox.IsEnabled = false;
        if (formEnabled)
        {
            UpdateLinkedFields();
        }
    }

    private string NextPoolName()
    {
        var index = _working.StoragePools.Count(item => !item.IsPrimordial) + 1;
        return $"Pool{index:00}";
    }

    /// <summary>
    /// The research defaults are changeable, but 256K interleave and ReFS are
    /// outside the tested recommendation and are never applied silently
    /// (V0.47 design §8).
    /// </summary>
    private async Task<bool> ConfirmUnrecommendedAsync()
    {
        if (new[] { _performanceInterleaveBox, _capacityInterleaveBox, _scmInterleaveBox }
                .Any(box => (box.SelectedItem as string) == "256K")
            && !await ConfirmAsync(
                Text("256K 交织警告", "256K interleave warning"),
                Text(
                    "256K 交织不在当前测试推荐中。当前测试推荐为 64K 交织 + 64K NTFS 簇。确定继续？",
                    "256K interleave is outside the tested recommendation (64K interleave + 64K NTFS cluster). Continue anyway?")))
        {
            return false;
        }

        if (string.Equals(_fileSystemBox.SelectedItem as string, "ReFS", StringComparison.OrdinalIgnoreCase)
            && !await ConfirmAsync(
                Text("ReFS 提示", "ReFS notice"),
                Text(
                    "ReFS 没有与 NTFS 64K 同等的长期测试证据。确定继续？",
                    "ReFS has no long-run evidence equivalent to NTFS 64K. Continue anyway?")))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Human-language apply preview (V0.47 design §5.3, §7): the single
    /// Apply shows the user a step list in the UI language and never dumps raw
    /// commands on the structure page.
    /// </summary>
    private async Task<bool> ConfirmApplyPreviewAsync(StoragePoolInfo pool, bool isDraft)
    {
        var steps = BuildApplyPreviewSteps(pool, isDraft);
        if (steps.Count == 0)
        {
            return false;
        }

        var lines = string.Join("\n", steps.Select(step => "• " + step));
        return await ConfirmAsync(ViewModel.Localization["ApplyPreviewTitle"], lines);
    }

    private IReadOnlyList<string> BuildApplyPreviewSteps(StoragePoolInfo pool, bool isDraft)
    {
        var steps = new List<string>();
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var committed = ViewModel.ActiveSnapshot;
        var poolName = _poolNameBox.Text.Trim();

        if (isDraft)
        {
            var memberCount = pool.MemberPhysicalDiskIds.Count;
            steps.Add(zh
                ? $"创建存储池“{poolName}”，加入 {memberCount} 个物理磁盘。"
                : $"Create storage pool \"{poolName}\" and join {memberCount} physical disk(s).");
            foreach (var (media, resiliency, interleave) in TierPreviewLines(pool))
            {
                var tierName = zh
                    ? media switch
                    {
                        "SSD" => "SSD 性能层",
                        "HDD" => "HDD 容量层",
                        _ => "SCM 专用层"
                    }
                    : media switch
                    {
                        "SSD" => "SSD performance tier",
                        "HDD" => "HDD capacity tier",
                        _ => "SCM dedicated tier"
                    };
                steps.Add(zh
                    ? $"{tierName}：{ResiliencyName(resiliency)}，交织 {interleave}。"
                    : $"{tierName}: {resiliency}, {interleave} interleave.");
            }

            var draftVirtualName = _virtualDiskNameBox.Text.Trim();
            steps.Add(zh
                ? $"在该池上创建虚拟磁盘“{draftVirtualName}”。"
                : $"Create virtual disk \"{draftVirtualName}\" on the pool.");
            var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
            if (_autoPartitionBox.IsChecked == true)
            {
                steps.Add(zh
                    ? $"创建 {fileSystem} 用户分区并格式化（簇 {_clusterBox.SelectedItem}）。"
                    : $"Create and format a {fileSystem} user partition ({_clusterBox.SelectedItem} cluster).");
            }
            else
            {
                steps.Add(zh
                    ? "虚拟磁盘保持未初始化（RAW），稍后在磁盘/分区页初始化。"
                    : "Leave the virtual disk RAW; initialize it on the Disk/partition page.");
            }

            return steps;
        }

        foreach (var disk in _working.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId))
            {
                continue;
            }

            var name = disk.FriendlyName;
            var committedDisk = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            var joined = string.Equals(disk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase);
            var wasInPool = committedDisk is not null
                && string.Equals(committedDisk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase);
            if (joined && !wasInPool)
            {
                steps.Add(zh ? $"将磁盘“{name}”加入该池。" : $"Add disk \"{name}\" to this pool.");
                continue;
            }

            if (wasInPool && !joined)
            {
                steps.Add(zh ? $"将磁盘“{name}”移出该池。" : $"Remove disk \"{name}\" from this pool.");
                continue;
            }

            var assigned = EditWorkspace.DiskIsAssignedToTier(_working, disk.StableId);
            var wasAssigned = committedDisk is not null
                && EditWorkspace.DiskIsAssignedToTier(committed, disk.StableId);
            if (wasInPool && wasAssigned && !assigned)
            {
                steps.Add(zh ? $"将磁盘“{name}”退层，保留在池的未分配区域。" : $"Remove disk \"{name}\" from its tier, keeping it unallocated in the pool.");
            }
            else if (wasInPool && !wasAssigned && assigned)
            {
                steps.Add(zh ? $"将磁盘“{name}”重新加入匹配层。" : $"Reassign disk \"{name}\" to its matching tier.");
            }
        }

        var vdisk = _working.VirtualDisks.FirstOrDefault(item => item.PoolStableId == pool.StableId);
        var committedVdisk = committed.VirtualDisks.FirstOrDefault(item => item.PoolStableId == pool.StableId);
        if (!string.Equals(poolName, pool.FriendlyName, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(zh
                ? $"将存储池重命名为“{poolName}”。"
                : $"Rename the pool to \"{poolName}\".");
        }

        var virtualName = _virtualDiskNameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(virtualName)
            && (committedVdisk is null
                || !string.Equals(virtualName, committedVdisk.FriendlyName, StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add(zh
                ? $"将虚拟磁盘重命名为“{virtualName}”。"
                : $"Rename the virtual disk to \"{virtualName}\".");
        }

        if (FormRebuildsPool(pool))
        {
            steps.Add(zh
                ? "重建存储层参数（冗余、交织、容量、列数）。"
                : "Rebuild tier parameters (resiliency, interleave, capacity, columns).");
            if (vdisk is not null && !string.IsNullOrWhiteSpace(vdisk.FriendlyName))
            {
                var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
                steps.Add(zh
                    ? $"将虚拟磁盘“{vdisk.FriendlyName}”的分区格式化为 {fileSystem}（簇 {_clusterBox.SelectedItem}）。"
                    : $"Format the partition on \"{vdisk.FriendlyName}\" as {fileSystem} ({_clusterBox.SelectedItem} cluster).");
            }
        }

        return steps;
    }

    private IEnumerable<(string Media, string Resiliency, string Interleave)> TierPreviewLines(StoragePoolInfo pool)
    {
        foreach (var (media, resiliencyBox, interleaveBox) in new[]
                 {
                     ("SSD", _performanceResiliencyBox, _performanceInterleaveBox),
                     ("HDD", _capacityResiliencyBox, _capacityInterleaveBox),
                     ("SCM", _scmResiliencyBox, _scmInterleaveBox)
                 })
        {
            var memberCount = pool.MemberPhysicalDiskIds.Count(id =>
            {
                var disk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == id);
                return disk is not null && EditWorkspace.NormalizeMedia(disk.MediaType) == media;
            });
            if (memberCount == 0)
            {
                continue;
            }

            yield return (media, resiliencyBox.SelectedItem as string ?? "Mirror",
                interleaveBox.SelectedItem as string ?? "64K");
        }
    }

    private string ResiliencyName(string resiliency) =>
        resiliency switch
        {
            "Simple" => "无冗余 (Simple)",
            "Mirror" => "镜像 (Mirror)",
            "Parity" => "奇偶校验 (Parity)",
            _ => resiliency
        };

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (pool is null || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var isDraftPool = EditWorkspace.IsDraftPool(pool.StableId);
        var appliedUpdate = false;
        var hasData = false;
        var formChanged = false;
        if (!isDraftPool)
        {
            hasData = EditWorkspace.PoolHoldsStoredData(_working, pool.StableId);
            if (hasData && FormRebuildsPool(pool))
            {
                await ShowMessageAsync(
                    ViewModel.Localization["RebuildBlockedTitle"],
                    ViewModel.Localization["RebuildBlockedMessage"]);
                return;
            }

            formChanged = PoolFormHasAnyChange(pool);
            // A data-free pool may be rebuilt from the property form; never
            // apply an unrecommended 256K or ReFS choice to that rebuild
            // silently.
            if (!hasData && formChanged && !await ConfirmUnrecommendedAsync())
            {
                return;
            }
        }

        // One apply, one human-language preview: show what will change before
        // anything is persisted to the simulation document.
        if (!await ConfirmApplyPreviewAsync(pool, isDraftPool))
        {
            return;
        }

        var committed = ViewModel.ActiveSnapshot;
        foreach (var disk in _working.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId))
            {
                continue;
            }

            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null || original.PoolStableId == disk.PoolStableId)
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.MovePhysicalDisk,
                    disk.StableId,
                    Name: disk.PoolStableId ?? string.Empty)) is null)
            {
                return;
            }
        }

        committed = ViewModel.ActiveSnapshot;
        foreach (var disk in _working.PhysicalDisks)
        {
            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null
                || !string.Equals(original.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase)
                || EditWorkspace.DiskIsAssignedToTier(_working, disk.StableId)
                || !EditWorkspace.DiskIsAssignedToTier(committed, disk.StableId))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.EvictPhysicalDiskFromTiers,
                    disk.StableId)) is null)
            {
                return;
            }
        }

        committed = ViewModel.ActiveSnapshot;
        foreach (var disk in _working.PhysicalDisks)
        {
            if (!EditWorkspace.DiskNeedsSamePoolTierAssignment(_working, committed, disk.StableId)
                || string.IsNullOrEmpty(disk.PoolStableId))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.MovePhysicalDisk,
                    disk.StableId,
                    Name: disk.PoolStableId)) is null)
            {
                return;
            }
        }

        if (isDraftPool)
        {
            if (await ApplyAsync(
                    BuildPoolRequest(SimulationOperationKind.CreateTieredPool, "primordial", pool)) is null)
            {
                return;
            }
        }
        else if (formChanged)
        {
            // One apply covers structure and parameters: property edits on an
            // existing pool are submitted together with the membership
            // changes already applied above.
            var pending = _working;
            var request = hasData
                ? new SimulationOperationRequest(
                    SimulationOperationKind.UpdateStoragePool,
                    pool.StableId,
                    Name: _poolNameBox.Text.Trim(),
                    VirtualDiskName: _virtualDiskNameBox.Text.Trim())
                : BuildPoolRequest(SimulationOperationKind.UpdateStoragePool, pool.StableId, pool);
            if (await ApplyAsync(request) is null)
            {
                return;
            }

            _working = EditWorkspace.RestoreWorkingMembership(ViewModel.ActiveSnapshot, pending);
            appliedUpdate = true;
        }

        var leftover = _working.StoragePools
            .Where(item => EditWorkspace.IsDraftPool(item.StableId) && item.StableId != pool.StableId)
            .ToArray();
        var preserveForm = !isDraftPool;
        _working = ViewModel.ActiveSnapshot;
        foreach (var draft in leftover)
        {
            var members = draft.MemberPhysicalDiskIds
                .Where(id => _working.PhysicalDisks.Any(disk =>
                    disk.StableId == id
                    && _working.StoragePools.Any(candidate =>
                        candidate.IsPrimordial && candidate.MemberPhysicalDiskIds.Contains(id))))
                .ToArray();
            if (members.Length == 0
                || _working.StoragePools.Any(item => EditWorkspace.IsDraftPool(item.StableId)))
            {
                // Single-draft rule: leftover members stay in the primordial
                // pool (visible in its Unallocated group) instead of
                // silently creating another draft.
                continue;
            }

            _working = EditWorkspace.InsertDraftPool(_working, draft.FriendlyName);
            var created = _working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId));
            foreach (var member in members)
            {
                _working = EditWorkspace.MoveDiskToPool(_working, member, created.StableId);
            }
        }

        _selectedPoolId = EditWorkspace.IsDraftPool(pool.StableId)
            ? _working.StoragePools.LastOrDefault(item => !item.IsPrimordial)?.StableId
            : pool.StableId;
        if (preserveForm)
        {
            if (appliedUpdate)
            {
                RefreshAll();
            }
            else
            {
                RefreshTopology();
                UpdateButtonState();
            }
        }
        else
        {
            RefreshAll();
        }
    }

    private bool PoolFormHasAnyChange(StoragePoolInfo pool)
    {
        var vdisk = _working.VirtualDisks.FirstOrDefault(item => item.PoolStableId == pool.StableId);
        if (!string.Equals(
                _poolNameBox.Text.Trim(),
                pool.FriendlyName.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentVirtualName = vdisk?.FriendlyName ?? pool.FriendlyName;
        if (!string.Equals(
                _virtualDiskNameBox.Text.Trim(),
                currentVirtualName.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return FormRebuildsPool(pool);
    }

    private async void DeleteVdisk_Click(object sender, RoutedEventArgs e)
    {
        var vdisk = _working.VirtualDisks.FirstOrDefault(item => item.StableId == _selectedPoolVdiskId);
        var pool = vdisk is null
            ? null
            : _working.StoragePools.FirstOrDefault(item => item.StableId == vdisk.PoolStableId);
        if (vdisk is null
            || pool is null
            || pool.IsPrimordial
            || !EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId)
            || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var holdsData = EditWorkspace.DiskHoldsStoredData(_working, vdisk.StableId, isVirtualDisk: true);
        if (holdsData)
        {
            if (!await ConfirmAsync(
                    Text("删除虚拟磁盘", "Delete virtual disk"),
                    Text(
                        "该虚拟磁盘上有已使用的数据。删除会移除该虚拟磁盘及其卷。1.0 界面不会再提供创建第二个虚拟磁盘的操作。确定继续？",
                        "This virtual disk holds used data. Deleting removes the virtual disk and its volume, and 1.0 will not create a second virtual disk again. Continue anyway?")))
            {
                return;
            }
        }
        else if (!await ConfirmAsync(
                     Text("删除虚拟磁盘", "Delete virtual disk"),
                     Text(
                         "从该池删除此虚拟磁盘及其卷？池中其它虚拟磁盘不受影响。",
                         "Remove this virtual disk and its volumes from the pool? Other virtual disks in the pool are not affected.")))
        {
            return;
        }

        _selectedPoolVdiskId = null;
        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.DeleteVirtualDisk,
                vdisk.StableId)) is null)
        {
            return;
        }

        _working = ViewModel.ActiveSnapshot;
        _selectedPoolId = pool.StableId;
        RefreshAll();
    }

    private SimulationOperationRequest BuildPoolRequest(
        SimulationOperationKind kind,
        string target,
        StoragePoolInfo pool)
    {
        var members = kind == SimulationOperationKind.CreateTieredPool
            ? pool.MemberPhysicalDiskIds
            : null;
        return new SimulationOperationRequest(
            kind,
            target,
            Name: _poolNameBox.Text.Trim(),
            FileSystem: _fileSystemBox.SelectedItem as string ?? "NTFS",
            AllocationUnitSize: ParseSize(_clusterBox.SelectedItem as string ?? "64K"),
            MemberDiskIds: members,
            VirtualDiskName: _virtualDiskNameBox.Text.Trim(),
            PerformanceResiliency: _performanceResiliencyBox.SelectedItem as string,
            PerformanceInterleaveBytes: ParseSize(_performanceInterleaveBox.SelectedItem as string ?? "64K"),
            PerformanceSizeBytes: ParseGigabytes(_performanceSizeBox.Text),
            PerformanceDataCopies: ParseInt(_performanceCopiesBox.Text),
            CapacityResiliency: _capacityResiliencyBox.SelectedItem as string,
            CapacityInterleaveBytes: ParseSize(_capacityInterleaveBox.SelectedItem as string ?? "64K"),
            CapacitySizeBytes: ParseGigabytes(_capacitySizeBox.Text),
            CapacityColumns: ParseInt(_capacityColumnsBox.Text),
            CapacityToleratedFailures: ParseInt(_capacityFailuresBox.Text),
            ScmResiliency: _scmResiliencyBox.SelectedItem as string,
            ScmInterleaveBytes: ParseSize(_scmInterleaveBox.SelectedItem as string ?? "64K"),
            ScmDataCopies: ParseInt(_scmCopiesBox.Text),
            CreatePartition: _autoPartitionBox.IsChecked);
    }

    private async void Evict_Click(object sender, RoutedEventArgs e)
    {
        var disk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == _selectedPoolDiskId);
        if (disk is null || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        switch (EditWorkspace.ClassifyDiskEvict(disk))
        {
            case EditWorkspace.DiskEvictCheck.DeniedSystem:
                await ShowMessageAsync(
                    ViewModel.Localization["SystemDiskCannotEvictTitle"],
                    ViewModel.Localization["SystemDiskCannotEvictMessage"]);
                return;
            case EditWorkspace.DiskEvictCheck.ConfirmPageFile:
                if (!await ConfirmAsync(
                        ViewModel.Localization["RemovePageFileTitle"],
                        ViewModel.Localization["RemovePageFileMessage"]))
                {
                    return;
                }

                _working = EditWorkspace.ClearEvictableSpecialRoles(_working, disk.StableId);
                break;
            case EditWorkspace.DiskEvictCheck.ConfirmCrashDump:
                if (!await ConfirmAsync(
                        ViewModel.Localization["RemoveCrashDumpTitle"],
                        ViewModel.Localization["RemoveCrashDumpMessage"]))
                {
                    return;
                }

                _working = EditWorkspace.ClearEvictableSpecialRoles(_working, disk.StableId);
                break;
        }

        try
        {
            _working = EditWorkspace.EvictDiskToUnallocated(_working, disk.StableId);
        }
        catch (InvalidOperationException exception)
        {
            await ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
            return;
        }

        RefreshTopology();
        FillPoolForm();
        UpdateButtonState();
    }

    private bool FormRebuildsPool(StoragePoolInfo pool)
    {
        var ssd = Tier(pool.StableId, "SSD");
        var hdd = Tier(pool.StableId, "HDD");
        var scm = Tier(pool.StableId, "SCM");
        var partition = PartitionForPool(pool.StableId);
        return !SameToken(_performanceResiliencyBox, ssd?.ResiliencySettingName ?? "Mirror")
            || !SameToken(_capacityResiliencyBox, hdd?.ResiliencySettingName ?? "Parity")
            || !SameToken(_scmResiliencyBox, scm?.ResiliencySettingName ?? "Mirror")
            || InterleaveToken(ssd?.Interleave) != (_performanceInterleaveBox.SelectedItem as string)
            || InterleaveToken(hdd?.Interleave) != (_capacityInterleaveBox.SelectedItem as string)
            || InterleaveToken(scm?.Interleave) != (_scmInterleaveBox.SelectedItem as string)
            || _performanceSizeBox.Text.Trim() != ToGigabytes(ssd?.Size)
            || _capacitySizeBox.Text.Trim() != ToGigabytes(hdd?.Size)
            || _scmSizeBox.Text.Trim() != ToGigabytes(scm?.Size)
            || _performanceCopiesBox.Text.Trim() != (ssd?.NumberOfDataCopies ?? 2).ToString()
            || _capacityFailuresBox.Text.Trim() != (hdd?.PhysicalDiskRedundancy ?? 1).ToString()
            || _scmCopiesBox.Text.Trim() != (scm?.NumberOfDataCopies ?? 2).ToString()
            || _capacityColumnsBox.Text.Trim() != (hdd?.NumberOfColumns
                ?? EditWorkspace.RecommendedCapacityColumns(Members(hdd))).ToString()
            || (_fileSystemBox.SelectedItem as string) != (partition?.FileSystem ?? "NTFS")
            || (_clusterBox.SelectedItem as string) != ClusterToken(partition?.AllocationUnitSize);
    }

    private static bool SameToken(ComboBox box, string value) =>
        string.Equals(box.SelectedItem as string, value, StringComparison.OrdinalIgnoreCase);

    private static string InterleaveToken(long? bytes) =>
        $"{Math.Max(1, (bytes ?? 65536) / 1024)}K";

    private static string ClusterToken(long? bytes) =>
        bytes switch
        {
            4096 => "4K",
            8192 => "8K",
            16384 => "16K",
            32768 => "32K",
            _ => "64K"
        };

    private async void Dissolve_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (pool is null)
        {
            return;
        }

        if (EditWorkspace.IsDraftPool(pool.StableId))
        {
            _working = EditWorkspace.DiscardDraftPool(_working, pool.StableId);
            _selectedPoolId = null;
            _selectedPoolDiskId = null;
            _selectedPoolVdiskId = null;
            RefreshAll();
            return;
        }

        if (!await ConfirmAsync(
                ViewModel.Localization["DissolvePoolTitle"],
                ViewModel.Localization["DissolvePoolMessage"]))
        {
            return;
        }

        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.DissolveStoragePool,
                pool.StableId)) is null)
        {
            return;
        }

        _working = ViewModel.ActiveSnapshot;
        _selectedPoolId = null;
        _selectedPoolDiskId = null;
        _selectedPoolVdiskId = null;
        RefreshAll();
    }
}
