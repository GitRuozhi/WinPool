using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationKind = WinPool.Application.SimulationEditKind;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;
using SimulationOperationResult = WinPool.Application.SimulationEditReceipt;

namespace WinPool_App;

public sealed partial class EditPage : Page
{
    private WorkspaceViewModel ViewModel { get; set; } = null!;
    private StorageSnapshot _working = StorageSnapshot.Empty("edit");
    private string? _selectedDiskId;
    private string? _selectedPartitionId;
    private long? _selectedUnallocatedOffset;
    private long? _selectedUnallocatedSize;
    private string? _selectedPoolId;
    private TopologyEditInteraction _upperInteraction = null!;
    private TopologyEditInteraction _lowerInteraction = null!;
    private bool _formBuilt;
    private bool _fillingForm;

    private readonly Button _executeButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _dissolveButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
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
            _selectedPoolId = ResolvePoolId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.ActiveSnapshot;
        _upperInteraction = new TopologyEditInteraction(IsUpperSelected, OnUpperSelected, false);
        _lowerInteraction = new TopologyEditInteraction(
            IsLowerSelected,
            OnLowerSelected,
            ViewModel.IsUsingSimulatedInventory,
            OnDiskDropped);
        LocalizeChrome();
        EnsureForm();
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
        _executeButton.Click += Execute_Click;
        _dissolveButton.Click += Dissolve_Click;

        var row = 0;
        AddFormRow(row++, string.Empty, _executeButton);
        AddFormRow(row++, string.Empty, _dissolveButton);
        AddFormRow(row++, "PoolName", _poolNameBox);
        AddFormRow(row++, "VirtualDiskName", _virtualDiskNameBox);
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
        RefreshUpper();
        RefreshLower();
        FillPoolForm();
        UpdateButtonState();
    }

    private bool IsUpperSelected(TopologyNodeViewModel node) =>
        node.Unit.StableId == _selectedDiskId
        || node.Unit.StableId == _selectedPartitionId
        || (EditWorkspace.IsUnallocated(node.Unit.StableId)
            && EditWorkspace.TryParseUnallocated(node.Unit.StableId, out var disk, out var offset, out _)
            && disk == _selectedDiskId
            && offset == _selectedUnallocatedOffset);

    private bool IsLowerSelected(TopologyNodeViewModel node) =>
        node.Unit.Kind == StorageUnitKind.StoragePool && node.Unit.StableId == _selectedPoolId;

    private void OnUpperSelected(TopologyNodeViewModel node)
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

        RefreshUpper();
        UpdateButtonState();
    }

    private void OnLowerSelected(TopologyNodeViewModel node)
    {
        if (EditWorkspace.IsPlus(node.Unit.StableId))
        {
            _working = EditWorkspace.InsertDraftPool(_working, NextPoolName());
            _selectedPoolId = _working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
            RefreshLower();
            FillPoolForm();
            UpdateButtonState();
            return;
        }

        _selectedPoolId = node.Unit.Kind == StorageUnitKind.StoragePool
            ? node.Unit.StableId
            : node.Unit.ParentStableId;
        RefreshLower();
        FillPoolForm();
        UpdateButtonState();
    }

    private void OnDiskDropped(string diskId, string poolId)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            return;
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
                _working = EditWorkspace.InsertDraftPool(_working, NextPoolName());
                poolId = _working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
                _selectedPoolId = poolId;
            }

            _working = EditWorkspace.MoveDiskToPool(_working, diskId, poolId);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
            return;
        }

        RefreshLower();
        FillPoolForm();
        UpdateButtonState();
    }

    private long UnallocatedIgnoreBytes =>
        Math.Max(0, ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);

    private void UpperScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(320, e.NewSize.Width - 20);
        UpperTopologyControl.Width = width;
        ViewModel.UpdateTopologyViewportWidth(width);
    }

    private void LowerScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(320, e.NewSize.Width - 20);
        LowerTopologyControl.Width = width;
        ViewModel.UpdateTopologyViewportWidth(width);
    }

    private void RefreshUpper()
    {
        var nodes = EditWorkspace.ProjectPartitionWorkspace(_working, UnallocatedIgnoreBytes);
        UpperTopologyControl.ItemsSource = nodes
            .Select(node => new TopologyNodeViewModel(
                EditWorkspace.ToManageView(node, ViewModel.ActiveDocument.SystemId, $"edit-disk:{node.Unit.StableId}"),
                ViewModel,
                _working,
                _upperInteraction))
            .ToArray();
        var selected = _working.Partitions.FirstOrDefault(item => item.StableId == _selectedPartitionId);
        SelectedPartitionInfo.Text = selected is null
            ? _selectedUnallocatedOffset is null
                ? string.Empty
                : $"{ViewModel.Localization["Unallocated"]} · {TopologyProjector.FormatBytes(_selectedUnallocatedSize ?? 0)}"
            : $"{ViewModel.PartitionTypeName(selected.Type)} · {(string.IsNullOrWhiteSpace(selected.FileSystem) ? "RAW" : selected.FileSystem)} · {TopologyProjector.FormatBytes(selected.Size)}";
    }

    private void RefreshLower()
    {
        var root = EditWorkspace.ProjectPoolWorkspaceRoot(_working, UnallocatedIgnoreBytes);
        LowerTopologyControl.ItemsSource = new[]
        {
            new TopologyNodeViewModel(
                EditWorkspace.ToManageView(root, ViewModel.ActiveDocument.SystemId, "edit-pool-row"),
                ViewModel,
                _working,
                _lowerInteraction)
        };
    }

    private StoragePoolInfo? SelectedPool() =>
        _working.StoragePools.FirstOrDefault(item => item.StableId == _selectedPoolId);

    private void FillPoolForm()
    {
        _fillingForm = true;
        PoolFormGrid.Visibility = Visibility.Visible;
        var pool = SelectedPool();
        var showScm = EditWorkspace.HasScmDisk(_working);
        foreach (var element in _scmRows)
        {
            element.Visibility = showScm ? Visibility.Visible : Visibility.Collapsed;
        }

        if (pool is null || pool.IsPrimordial)
        {
            FillRecommendedDefaults();
            _fillingForm = false;
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
        _fillingForm = false;
    }

    private void FillRecommendedDefaults()
    {
        _poolNameBox.Text = NextPoolName();
        _virtualDiskNameBox.Text = _poolNameBox.Text;
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
        if (_fillingForm)
        {
            LinkResiliency(_performanceResiliencyBox, _performanceCopiesBox, _performanceFailuresBox, copiesMaster: true);
            LinkResiliency(_capacityResiliencyBox, _capacityCopiesBox, _capacityFailuresBox, copiesMaster: false);
            LinkResiliency(_scmResiliencyBox, _scmCopiesBox, _scmFailuresBox, copiesMaster: true);
            return;
        }

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
        var partition = _working.Partitions.FirstOrDefault(x => x.StableId == _selectedPartitionId);
        var primary = partition?.Type == "Primary" && partition is { IsBoot: false, IsSystem: false };
        var disk = _working.OsDisks.FirstOrDefault(x => x.StableId == _selectedDiskId);
        var diskEditable = simulated && disk is { IsBoot: false, IsSystem: false };
        ExtendButton.IsEnabled = simulated && primary == true;
        ShrinkButton.IsEnabled = simulated && primary == true;
        DeletePartitionButton.IsEnabled = simulated && primary == true;
        FormatButton.IsEnabled = simulated && primary == true;
        NewPartitionButton.IsEnabled = simulated && disk is { IsOffline: false };
        InitializeButton.IsEnabled = diskEditable;
        OfflineButton.IsEnabled = simulated && disk is not null;

        var pool = SelectedPool();
        var multi = pool is not null && EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId);
        _multiVdiskWarning.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        var isDraft = pool is not null && EditWorkspace.IsDraftPool(pool.StableId);
        _executeButton.Content = isDraft
            ? ViewModel.Localization["CreateNewPool"]
            : ViewModel.Localization["ExecuteModify"];
        _dissolveButton.Content = ViewModel.Localization["DissolvePool"];
        _executeButton.IsEnabled = simulated
            && pool is { IsPrimordial: false }
            && !multi
            && (!isDraft || pool.MemberPhysicalDiskIds.Count > 0);
        _dissolveButton.IsEnabled = simulated && pool is { IsPrimordial: false };
        var formEnabled = simulated && pool is { IsPrimordial: false } && !multi;
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

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (pool is null || !ViewModel.IsUsingSimulatedInventory)
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

        SimulationOperationRequest request;
        if (EditWorkspace.IsDraftPool(pool.StableId))
        {
            request = BuildPoolRequest(SimulationOperationKind.CreateTieredPool, "primordial", pool);
        }
        else
        {
            request = BuildPoolRequest(SimulationOperationKind.UpdateStoragePool, pool.StableId, pool);
        }

        if (await ApplyAsync(request) is null)
        {
            return;
        }

        var leftover = _working.StoragePools
            .Where(item => EditWorkspace.IsDraftPool(item.StableId) && item.StableId != pool.StableId)
            .ToArray();
        _working = ViewModel.ActiveSnapshot;
        foreach (var draft in leftover)
        {
            var members = draft.MemberPhysicalDiskIds
                .Where(id => _working.PhysicalDisks.Any(disk =>
                    disk.StableId == id
                    && _working.StoragePools.Any(candidate =>
                        candidate.IsPrimordial && candidate.MemberPhysicalDiskIds.Contains(id))))
                .ToArray();
            if (members.Length == 0)
            {
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
            ScmDataCopies: ParseInt(_scmCopiesBox.Text));
    }

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
        RefreshAll();
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
            FontFamily = new FontFamily("Consolas"),
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

    private static long ParseSize(string token) =>
        token.TrimEnd('K', 'k') is var digits && long.TryParse(digits, out var value)
            ? value * 1024
            : 65536;

    private static int? ParseInt(string text) =>
        int.TryParse(text, out var value) ? value : null;

    private static long? ParseGigabytes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryParseGigabytes(text, out var gb))
        {
            return null;
        }

        return (long)(gb * 1024L * 1024L * 1024L);
    }

    private static bool TryParseGigabytes(string text, out double gigabytes)
    {
        gigabytes = 0;
        if (!double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || !double.IsFinite(parsed)
            || parsed <= 0)
        {
            return false;
        }

        gigabytes = parsed;
        return true;
    }

    private async Task<SimulationOperationResult?> ApplyAsync(SimulationOperationRequest request)
    {
        WinPool.Application.ApplicationResult<SimulationOperationResult> result;
        try
        {
            result = await ViewModel.ApplySimulationOperationAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            await ShowMessageAsync(Text("操作失败", "Operation failed"), exception.Message);
            return null;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            await ShowMessageAsync(
                Text("操作不可用", "Operation unavailable"),
                result.Messages.FirstOrDefault()?.UserTextKey
                    ?? Text("模拟操作未完成。", "The simulation operation did not complete."));
            return null;
        }

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
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
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
