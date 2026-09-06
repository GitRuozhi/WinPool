using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using WinPool.Application;

namespace WinPool.App.ViewModels;

public sealed record TopologyEditInteraction(
    Func<TopologyNodeViewModel, bool> IsSelected,
    Action<TopologyNodeViewModel> OnSelect,
    bool AllowDiskDrag,
    Action<string, string>? OnDiskDropped = null);

public sealed partial class TopologyNodeViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _owner;

    private readonly StorageSnapshot _snapshot;

    private readonly string _occurrenceKey;

    private readonly TopologyEditInteraction? _edit;

    public TopologyNodeViewModel(
        ManageTopologyNodeView node,
        WorkspaceViewModel owner,
        StorageSnapshot snapshot,
        TopologyEditInteraction? edit = null,
        bool isLayoutRoot = false)
    {
        _owner = owner;
        _snapshot = snapshot;
        _occurrenceKey = node.OccurrenceKey;
        _edit = edit;
        IsLayoutRoot = isLayoutRoot;
        var unit = ToStorageUnit(node, snapshot);
        ObjectId = node.Id;
        Role = node.Role;
        Unit = unit with { DisplayName = LocalizeName(unit, owner) };
        Summary = LocalizeSummary(node, owner, snapshot);
        TypeLabel = LocalizeType(unit, owner, snapshot);
        IsReference = node.IsReference;
        IsSelectable = node.IsSelectable;
        ChildrenLayout = node.ChildrenLayout switch
        {
            ManageTopologyLayout.Stack => TopologyChildrenLayout.Stack,
            ManageTopologyLayout.Flow => TopologyChildrenLayout.Flow,
            ManageTopologyLayout.WeightedFlow => TopologyChildrenLayout.WeightedFlow,
            _ => throw new ArgumentOutOfRangeException(nameof(node))
        };
        LayoutWeight = node.LayoutWeight;
        NoWrapChildren = node.NoWrapChildren;
        DistributeByCapacity = node.DistributeByCapacity;
        CapacityWeights = node.CapacityWeights;
        StructureModifiable = node.StructureModifiable;
        AdaptiveHeaderEnabled = node.AdaptiveHeaderEnabled;
        _isExpanded = unit.Kind == StorageUnitKind.VirtualDiskGroup
            ? true
            : owner.GetExpandedState(_occurrenceKey, node.IsExpanded);
        Children = node.Children
            .Select(child => new TopologyNodeViewModel(child, owner, snapshot, edit))
            .ToList();
        (BadgeText, BadgeIsWarning) = ComputeBadge(owner, snapshot, unit);
        IsWindowsBacked = ComputeIsWindowsBacked(snapshot, unit);
        var physical = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == unit.StableId);
        IsDragSource = edit?.AllowDiskDrag == true
            && unit.Kind == StorageUnitKind.PhysicalDisk
            && physical is { IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false };
        IsDropTarget = edit?.AllowDiskDrag == true && unit.Kind == StorageUnitKind.StoragePool;
    }

    public bool IsDragSource { get; }

    public bool IsDropTarget { get; }

    public TopologyEditInteraction? EditInteraction => _edit;

    public string? ResolvePoolDropId()
    {
        if (Unit.Kind == StorageUnitKind.StoragePool)
        {
            return Unit.StableId;
        }

        return Unit.ParentStableId;
    }

    public string BadgeText { get; }

    public bool BadgeIsWarning { get; }

    public bool IsWindowsBacked { get; }

    public Visibility WindowsMarkerVisibility =>
        IsWindowsBacked ? Visibility.Visible : Visibility.Collapsed;

    private static bool ComputeIsWindowsBacked(StorageSnapshot snapshot, StorageUnitRef unit)
    {
        switch (unit.Kind)
        {
            case StorageUnitKind.PhysicalDisk:
                return snapshot.OsDisks.Any(
                    x => x.PhysicalDiskStableId == unit.StableId && (x.IsSystem || x.IsBoot));
            case StorageUnitKind.Partition:
                var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId);
                return partition is { IsBoot: true } or { IsSystem: true };
            case StorageUnitKind.OsDisk:
                var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == unit.StableId);
                return osDisk is { IsSystem: true } or { IsBoot: true };
            default:
                return false;
        }
    }

    public Visibility BadgeVisibility =>
        string.IsNullOrEmpty(BadgeText) ? Visibility.Collapsed : Visibility.Visible;

    private static (string Text, bool IsWarning) ComputeBadge(
        WorkspaceViewModel owner,
        StorageSnapshot snapshot,
        StorageUnitRef unit)
    {
        switch (unit.Kind)
        {
            case StorageUnitKind.PhysicalDisk:
                var physical = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == unit.StableId);
                if (physical is not null
                    && StorageFindingInspector.IsUnhealthy(physical.HealthStatus, physical.OperationalStatus))
                {
                    return (owner.Localization["Unhealthy"], true);
                }
                break;
            case StorageUnitKind.VirtualDisk:
                var virtualDisk = snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == unit.StableId);
                if (virtualDisk is not null
                    && StorageFindingInspector.IsUnhealthy(virtualDisk.HealthStatus, virtualDisk.OperationalStatus))
                {
                    return (owner.Localization["Unhealthy"], true);
                }
                break;
            case StorageUnitKind.StoragePool:
                var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId);
                if (pool is not null
                    && StorageFindingInspector.IsUnhealthy(pool.HealthStatus, pool.OperationalStatus))
                {
                    return (owner.Localization["Unhealthy"], true);
                }
                break;
            case StorageUnitKind.StorageTier:
                var tier = snapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId);
                if (tier is not null)
                {
                    var members = snapshot.PhysicalDisks.Where(
                        x => tier.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase));
                    if (members.Any(x => StorageFindingInspector.IsUnhealthy(x.HealthStatus, x.OperationalStatus)))
                    {
                        return (owner.Localization["Unhealthy"], true);
                    }
                }
                break;
            case StorageUnitKind.Partition:
                var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId);
                if (partition is not null
                    && StorageFindingInspector.IsUnhealthy(partition.HealthStatus, partition.OperationalStatus))
                {
                    return (owner.Localization["Unhealthy"], true);
                }
                break;
        }
        return (string.Empty, false);
    }

    public StorageUnitRef Unit { get; }

    public WinPool.Domain.StorageObjectId ObjectId { get; }

    public ManageObjectRole Role { get; }

    public string Summary { get; }

    public string TypeLabel { get; }

    public IReadOnlyList<TopologyNodeViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsReference { get; }

    public bool IsSelectable { get; }

    public TopologyChildrenLayout ChildrenLayout { get; }

    public int LayoutWeight { get; }

    /// <summary>Strip metadata forwarded to the layout engine (Plan §2).</summary>
    public bool NoWrapChildren { get; }

    public bool DistributeByCapacity { get; }

    public IReadOnlyList<double>? CapacityWeights { get; }

    public bool? StructureModifiable { get; }

    public Visibility ModifiabilityIconVisibility =>
        StructureModifiable is null ? Visibility.Collapsed : Visibility.Visible;

    private bool _hasPendingModifications;

    public Visibility ModifiableOkVisibility =>
        StructureModifiable == true && !_hasPendingModifications ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ModifiableNoVisibility =>
        StructureModifiable == false ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ModifiablePendingVisibility =>
        _hasPendingModifications ? Visibility.Visible : Visibility.Collapsed;

    public string ModifiableOkName => _owner.Localization["StructureModifiable"];

    public string ModifiableNoName => _owner.Localization["StructureNotModifiable"];

    public string ModifiablePendingName => _owner.Localization["StructurePendingModification"];

    /// <summary>Call outside layout passes; raises the icon notifications.</summary>
    public void SetPendingModifications(bool value)
    {
        if (_hasPendingModifications == value)
        {
            return;
        }

        _hasPendingModifications = value;
        OnPropertyChanged(nameof(ModifiableOkVisibility));
        OnPropertyChanged(nameof(ModifiableNoVisibility));
        OnPropertyChanged(nameof(ModifiablePendingVisibility));
    }

    /// <summary>
    /// Named single-line header threshold (Plan §2.7): the assigned width at
    /// or above which an adaptive header collapses to one line. The exact
    /// value is confirmed with a screenshot during acceptance.
    /// </summary>
    public const double SingleLineHeaderThreshold = 420;

    /// <summary>Width this node's slot received from its parent row.</summary>
    public double AssignedWidth { get; private set; } = double.NaN;

    public bool AdaptiveHeaderEnabled { get; }

    public bool SingleLineHeader =>
        AdaptiveHeaderEnabled
        && !double.IsNaN(AssignedWidth)
        && AssignedWidth >= SingleLineHeaderThreshold;

    public Visibility SingleLineHeaderVisibility =>
        SingleLineHeader ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MultiLineHeaderVisibility =>
        SingleLineHeader ? Visibility.Collapsed : Visibility.Visible;

    public string SingleLineHeaderText =>
        string.Join(
          "  ",
          new[] { TypeLabel, Summary }
              .Where(text => !string.IsNullOrWhiteSpace(text)));

    public int LayoutUnitWidth { get; private set; } = 1;

    public int LayoutUnitHeight { get; private set; } = 1;

    public int LayoutFlowColumns { get; private set; }

    public double LayoutPixelWidth { get; private set; }

    public IReadOnlyList<IReadOnlyList<int>> LayoutRows { get; private set; } = [];

    public IReadOnlyList<double> LayoutChildWidths { get; private set; } = [];

    public double HostViewportWidth => _surfaceViewportWidth;

    private double _surfaceViewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;

    /// <summary>
    /// Updates this surface's own fallback host width. Only surface roots
    /// need to call this; there is no shared topology viewport state.
    /// </summary>
    public void SetSurfaceViewportWidth(double width)
    {
        var normalized = Math.Max(WorkspaceViewModel.MinSurfaceViewportWidth, width);
        if (Math.Abs(_surfaceViewportWidth - normalized) < 1)
        {
            return;
        }

        _surfaceViewportWidth = normalized;
        RefreshLayout();
    }

    /// <summary>
    /// True only for the root node of a topology surface (Manage system
    /// root, Edit pool row). Only this node's panel runs the layout engine;
    /// every nested panel consumes the cascaded slots.
    /// </summary>
    public bool IsLayoutRoot { get; }

    public void ApplyLayout(TopologyLayoutResult result, double assignedWidth = double.NaN)
    {
        LayoutUnitWidth = result.UnitWidth;
        LayoutUnitHeight = result.UnitHeight;
        LayoutFlowColumns = result.FlowColumns;
        LayoutPixelWidth = result.PixelWidth;
        LayoutRows = result.Rows;
        LayoutChildWidths = result.ChildWidths;
        AssignedWidth = assignedWidth;
        if (!IsExpanded)
        {
            return;
        }

        for (var i = 0; i < Children.Count && i < result.Children.Count; i++)
        {
            Children[i].ApplyLayout(result.Children[i], result.ChildWidths[i]);
        }
    }

    private bool? _lastSingleLine;

    /// <summary>
    /// Raises the header presentation notifications for this subtree.
    /// Must be called OUTSIDE the layout pass (deferred dispatch), never
    /// from within Measure/Arrange: changing visibility mid-layout
    /// re-enters layout and crashes WinUI.
    /// Change-guarded so repeated identical layouts raise nothing.
    /// </summary>
    public void NotifyLayoutApplied()
    {
        var singleLine = SingleLineHeader;
        if (_lastSingleLine != singleLine)
        {
            _lastSingleLine = singleLine;
            OnPropertyChanged(nameof(SingleLineHeader));
            OnPropertyChanged(nameof(SingleLineHeaderVisibility));
            OnPropertyChanged(nameof(MultiLineHeaderVisibility));
            OnPropertyChanged(nameof(SingleLineHeaderText));
        }

        foreach (var child in Children)
        {
            child.NotifyLayoutApplied();
        }
    }

    public bool IsSelected =>
        _edit?.IsSelected(this) ?? _owner.IsTopologySelected(ObjectId, Role);

    public int HeaderRow => 0;

    public int ChildrenRow => 1;

    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string ExpandAutomationName =>
        IsExpanded ? $"Collapse {Unit.DisplayName}" : $"Expand {Unit.DisplayName}";

    public Visibility ExpandButtonVisibility =>
        HasChildren && Unit.Kind is not StorageUnitKind.VirtualDiskGroup
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Layout-only container: the node keeps its tree slot so its children
    /// flow in a row, but it renders no card, padding, header, or hover.
    /// </summary>
    public bool IsInvisibleLayoutContainer =>
        Unit.Kind is StorageUnitKind.VirtualDiskGroup;

    public string TypeGlyph => Unit.Kind switch
    {
        StorageUnitKind.System => "\uE7F8",
        StorageUnitKind.StoragePool => "\uE8F1",
        StorageUnitKind.StorageTier or StorageUnitKind.DirectDiskGroup => "\uE8FD",
        StorageUnitKind.NetworkDisk or StorageUnitKind.NetworkDiskGroup => "\uE774",
        StorageUnitKind.OtherDiskGroup => "\uE8B7",
        StorageUnitKind.Partition => "\uE7C3",
        _ => "\uEDA2"
    };

    public Visibility HeaderVisibility =>
        Unit.Kind is StorageUnitKind.VirtualDiskGroup ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FlowChildrenVisibility =>
        IsExpanded && ChildrenLayout == TopologyChildrenLayout.Flow ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StackChildrenVisibility =>
        IsExpanded && ChildrenLayout == TopologyChildrenLayout.Stack ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WeightedChildrenVisibility =>
        IsExpanded && ChildrenLayout == TopologyChildrenLayout.WeightedFlow ? Visibility.Visible : Visibility.Collapsed;

    public double PreferredWidth => Unit.Kind switch
    {
        StorageUnitKind.System => double.NaN,
        StorageUnitKind.StoragePool => double.NaN,
        StorageUnitKind.StorageTier => double.NaN,
        StorageUnitKind.VirtualDisk => double.NaN,
        StorageUnitKind.NetworkDiskGroup or StorageUnitKind.OtherDiskGroup
            or StorageUnitKind.DirectDiskGroup or StorageUnitKind.VirtualDiskGroup => double.NaN,
        StorageUnitKind.PhysicalDisk => double.NaN,
        StorageUnitKind.NetworkDisk => double.NaN,
        StorageUnitKind.OsDisk => double.NaN,
        StorageUnitKind.Partition => double.NaN,
        _ => 170
    };

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        _owner.SaveExpandedState(_occurrenceKey, value);
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(ExpandAutomationName));
        OnPropertyChanged(nameof(FlowChildrenVisibility));
        OnPropertyChanged(nameof(StackChildrenVisibility));
        OnPropertyChanged(nameof(WeightedChildrenVisibility));
        if (value && Unit.Kind == StorageUnitKind.System)
        {
            _owner.SystemRootExpanded(this);
        }
    }

    [RelayCommand]
    private void Select()
    {
        if (!IsSelectable)
        {
            return;
        }

        if (_edit is not null)
        {
            _edit.OnSelect(this);
            return;
        }

        _owner.SelectManageTopologyNode(ObjectId, Role, _snapshot);
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (HasChildren && Unit.Kind is not StorageUnitKind.VirtualDiskGroup)
        {
            IsExpanded = !IsExpanded;
        }
    }

    public void RequestContextMenu(
        Microsoft.UI.Xaml.FrameworkElement target,
        Windows.Foundation.Point pointerPosition)
    {
        if (_edit is not null)
        {
            return;
        }

        _owner.NodeContextMenuRequested?.Invoke(
            new ManageObjectTarget(ObjectId, Role),
            target,
            pointerPosition);
    }

    public void RefreshSelection()
    {
        OnPropertyChanged(nameof(IsSelected));
        foreach (var child in Children)
        {
            child.RefreshSelection();
        }
    }

    public void RefreshLayout()
    {
        OnPropertyChanged(nameof(PreferredWidth));
        foreach (var child in Children)
        {
            child.RefreshLayout();
        }
    }

    public bool ExpandPathTo(ManageObjectTarget target)
    {
        if (ManageSelectionRules.SameTarget(
                new ManageObjectTarget(ObjectId, Role),
                target))
        {
            return true;
        }

        foreach (var child in Children)
        {
            if (!child.ExpandPathTo(target))
            {
                continue;
            }

            IsExpanded = true;
            return true;
        }

        return false;
    }

    private static string LocalizeName(StorageUnitRef unit, WorkspaceViewModel owner) =>
        unit.StableId switch
        {
            _ when EditWorkspace.IsPlus(unit.StableId) => "+",
            _ when EditWorkspace.IsPoolRow(unit.StableId) => string.Empty,
            _ when EditWorkspace.IsUnallocated(unit.StableId) => owner.Localization["Unallocated"],
            _ when EditWorkspace.IsPendingVirtualDisk(unit.StableId) => owner.Localization["NotCreated"],
            _ when unit.Kind == StorageUnitKind.NetworkDiskGroup => owner.Localization["Network"],
            _ when unit.Kind == StorageUnitKind.OtherDiskGroup => owner.Localization["Other"],
            _ when unit.Kind == StorageUnitKind.DirectDiskGroup => owner.Localization["UnallocatedLayer"],
            _ when unit.Kind == StorageUnitKind.VirtualDiskGroup => owner.Localization["VirtualDisks"],
            _ => unit.DisplayName
        };

    private static string LocalizeSummary(
        ManageTopologyNodeView node,
        WorkspaceViewModel owner,
        StorageSnapshot snapshot)
    {
        var unit = ToStorageUnit(node, snapshot);
        if (unit.Kind == StorageUnitKind.System)
        {
            var physical = snapshot.PhysicalDisks
                .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return TopologyProjector.JoinSummary(
                $"{snapshot.StoragePools.Count} {owner.Localization["StoragePool"]}",
                $"{physical.Count} {owner.Localization["PhysicalDisk"]}",
                snapshot.VirtualDisks.Count > 0
                    ? $"{snapshot.VirtualDisks.Count} {owner.Localization["VirtualDisk"]}"
                    : null,
                snapshot.NetworkDisks.Count > 0
                    ? $"{snapshot.NetworkDisks.Count} {owner.Localization["NetworkDisk"]}"
                    : null,
                TopologyProjector.FormatBytes(physical.Sum(x => x.Size)));
        }

        if (EditWorkspace.IsPlus(unit.StableId) || EditWorkspace.IsPendingVirtualDisk(unit.StableId))
        {
            return string.Empty;
        }

        if (EditWorkspace.IsUnallocated(unit.StableId)
            && EditWorkspace.TryParseUnallocated(unit.StableId, out _, out _, out var unallocatedSize))
        {
            return TopologyProjector.JoinSummary(
                owner.Localization["Unallocated"],
                TopologyProjector.FormatBytes(unallocatedSize));
        }

        if (unit.Kind == StorageUnitKind.StoragePool)
        {
            var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId);
            if (pool is null)
            {
                return node.Summary;
            }
            var members = snapshot.PhysicalDisks
                .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var virtualCount = snapshot.VirtualDisks.Count(x => x.PoolStableId == pool.StableId);
            return TopologyProjector.JoinSummary(
                $"{members.Count} {owner.Localization["PhysicalDisk"]}",
                virtualCount > 0
                    ? $"{virtualCount} {owner.Localization["VirtualDisk"]}"
                    : null,
                TopologyProjector.FormatBytes(members.Sum(x => x.Size)));
        }

        if (unit.Kind == StorageUnitKind.StorageTier)
        {
            var tier = snapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId);
            if (tier is null)
            {
                return node.Summary;
            }

            var members = snapshot.PhysicalDisks
                .Where(x => tier.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return TopologyProjector.JoinSummary(
                $"{members.Count} {owner.Localization["PhysicalDisk"]}",
                TopologyProjector.FormatBytes(members.Sum(x => x.Size)));
        }

        if (unit.Kind == StorageUnitKind.PhysicalDisk)
        {
            var disk = snapshot.PhysicalDisks.First(x => x.StableId == unit.StableId);
            var media = disk.MediaType is "HDD" or "SSD" or "SCM" ? disk.MediaType : owner.Localization["Unknown"];
            return TopologyProjector.JoinSummary(media, TopologyProjector.FormatBytes(disk.Size));
        }

        if (unit.Kind == StorageUnitKind.VirtualDisk)
        {
            var disk = snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == unit.StableId);
            if (disk is null)
            {
                return owner.Localization["NotCreated"];
            }

            return TopologyProjector.JoinSummary(
                disk.TierStableIds.Count > 0 ? "Tiered" : "Virtual",
                TopologyProjector.FormatBytes(disk.Size));
        }

        if (unit.Kind == StorageUnitKind.NetworkDisk)
        {
            var disk = snapshot.NetworkDisks.First(x => x.StableId == unit.StableId);
            return TopologyProjector.JoinSummary("Network", TopologyProjector.FormatBytes(disk.Size));
        }

        if (unit.Kind == StorageUnitKind.NetworkDiskGroup)
        {
            return TopologyProjector.JoinSummary(
                $"{snapshot.NetworkDisks.Count} {owner.Localization["NetworkDisk"]}",
                TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size)));
        }

        if (unit.Kind == StorageUnitKind.OtherDiskGroup)
        {
            var otherDisks = TopologyProjector.GetOtherOsDisks(snapshot);
            var otherIds = otherDisks.Select(x => x.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return TopologyProjector.JoinSummary(
                $"{otherDisks.Count} {owner.Localization["OtherDisk"]}",
                $"{snapshot.Partitions.Count(x => x.OsDiskStableId is not null && otherIds.Contains(x.OsDiskStableId))} {owner.Localization["Partition"]}",
                TopologyProjector.FormatBytes(otherDisks.Sum(x => x.Size)));
        }

        if (unit.Kind == StorageUnitKind.DirectDiskGroup)
        {
            var pool = snapshot.StoragePools.FirstOrDefault(
                x => $"group:direct:{x.StableId}".Equals(unit.StableId, StringComparison.OrdinalIgnoreCase));
            if (pool is null)
            {
                return string.Empty;
            }
            var tierMemberIds = snapshot.StorageTiers
                .Where(x => x.PoolStableId == pool.StableId)
                .SelectMany(x => x.MemberPhysicalDiskIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var direct = snapshot.PhysicalDisks
                .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase)
                    && !tierMemberIds.Contains(x.StableId))
                .ToList();
            return TopologyProjector.JoinSummary(
                $"{direct.Count} {owner.Localization["PhysicalDisk"]}",
                TopologyProjector.FormatBytes(direct.Sum(x => x.Size)));
        }

        if (unit.Kind == StorageUnitKind.VirtualDiskGroup)
        {
            var pool = snapshot.StoragePools.FirstOrDefault(
                x => $"group:vdisk:{x.StableId}".Equals(unit.StableId, StringComparison.OrdinalIgnoreCase));
            if (pool is null)
            {
                return string.Empty;
            }
            var count = snapshot.VirtualDisks.Count(x => x.PoolStableId == pool.StableId);
            return TopologyProjector.JoinSummary($"{count} {owner.Localization["VirtualDisk"]}");
        }

        if (unit.Kind == StorageUnitKind.Partition)
        {
            var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId);
            if (partition is null)
            {
                return node.Summary;
            }

            return TopologyProjector.JoinSummary(
                string.IsNullOrWhiteSpace(partition.FileSystem) ? owner.Localization["Unknown"] : partition.FileSystem,
                TopologyProjector.FormatBytes(partition.Size));
        }

        return node.Summary.Replace(" · ", "  ", StringComparison.Ordinal);
    }

    private static StorageUnitRef ToStorageUnit(ManageTopologyNodeView node, StorageSnapshot snapshot)
    {
        var kind = node.Role switch
        {
            ManageObjectRole.System => StorageUnitKind.System,
            ManageObjectRole.StorageSubsystem => StorageUnitKind.StorageSubsystem,
            ManageObjectRole.StoragePool => StorageUnitKind.StoragePool,
            ManageObjectRole.StorageTier => StorageUnitKind.StorageTier,
            ManageObjectRole.PhysicalDisk => StorageUnitKind.PhysicalDisk,
            ManageObjectRole.VirtualDisk => StorageUnitKind.VirtualDisk,
            ManageObjectRole.NetworkDisk => StorageUnitKind.NetworkDisk,
            ManageObjectRole.OsDisk => StorageUnitKind.OsDisk,
            ManageObjectRole.Partition => StorageUnitKind.Partition,
            ManageObjectRole.NetworkGroup => StorageUnitKind.NetworkDiskGroup,
            ManageObjectRole.OtherGroup => StorageUnitKind.OtherDiskGroup,
            ManageObjectRole.DirectDiskGroup => StorageUnitKind.DirectDiskGroup,
            ManageObjectRole.VirtualDiskGroup => StorageUnitKind.VirtualDiskGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(node))
        };
        var key = node.Id.ProviderKey;
        string? parent = kind switch
        {
            StorageUnitKind.StorageTier =>
                snapshot.StorageTiers.FirstOrDefault(item => item.StableId == key)?.PoolStableId,
            StorageUnitKind.PhysicalDisk =>
                snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == key)?.PoolStableId,
            StorageUnitKind.VirtualDisk =>
                snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == key)?.PoolStableId
                ?? (EditWorkspace.IsPendingVirtualDisk(key)
                    ? key[EditWorkspace.PendingVirtualPrefix.Length..]
                    : null),
            StorageUnitKind.Partition =>
                snapshot.Partitions.FirstOrDefault(item => item.StableId == key)?.OsDiskStableId
                ?? (EditWorkspace.TryParseUnallocated(key, out var osDisk, out _, out _) ? osDisk : null),
            StorageUnitKind.OsDisk =>
                snapshot.OsDisks.FirstOrDefault(item => item.StableId == key)?.PhysicalDiskStableId
                ?? snapshot.OsDisks.FirstOrDefault(item => item.StableId == key)?.VirtualDiskStableId,
            _ => null
        };
        return new StorageUnitRef(key, kind, node.DisplayName, node.IsStableIdentity, parent);
    }

    private static string LocalizeType(StorageUnitRef unit, WorkspaceViewModel owner, StorageSnapshot snapshot) =>
        unit.Kind switch
        {
            StorageUnitKind.System => owner.Localization["System"],
            StorageUnitKind.StoragePool =>
                SnapshotPoolType(unit, owner, snapshot),
            StorageUnitKind.StorageTier =>
                SnapshotTierType(unit, owner, snapshot),
            StorageUnitKind.PhysicalDisk => owner.Localization["PhysicalDisk"],
            StorageUnitKind.VirtualDisk => owner.Localization["VirtualDisk"],
            StorageUnitKind.NetworkDisk => owner.Localization["NetworkDisk"],
            StorageUnitKind.Partition => EditWorkspace.IsUnallocated(unit.StableId)
                ? owner.Localization["Unallocated"]
                : owner.PartitionTypeName(
                    snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId)?.Type ?? "Unknown"),
            StorageUnitKind.OsDisk => snapshot.OsDisks.FirstOrDefault(
                    item => item.StableId == unit.StableId) is { } osDisk
                ? string.IsNullOrWhiteSpace(osDisk.VirtualDiskStableId)
                    ? owner.Localization["PhysicalDisk"]
                    : owner.Localization["VirtualDisk"]
                : owner.Localization["OtherDisk"],
            StorageUnitKind.NetworkDiskGroup => owner.Localization["NetworkStorageGroup"],
            StorageUnitKind.OtherDiskGroup => owner.Localization["OtherStorageGroup"],
            StorageUnitKind.DirectDiskGroup => owner.Localization["UnallocatedLayer"],
            StorageUnitKind.VirtualDiskGroup => owner.Localization["VirtualDisks"],
            _ => unit.Kind.ToString()
        };

    private static string SnapshotPoolType(StorageUnitRef unit, WorkspaceViewModel owner, StorageSnapshot snapshot)
    {
        if (EditWorkspace.IsPlus(unit.StableId))
        {
            return owner.Localization["NewPool"];
        }

        return snapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId)?.IsPrimordial == true
            ? owner.Localization["OriginalPool"]
            : owner.Localization["StoragePool"];
    }

    private static string SnapshotTierType(StorageUnitRef unit, WorkspaceViewModel owner, StorageSnapshot snapshot)
    {
        var media = snapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId)?.MediaType;
        return media == "SSD"
            ? owner.Localization["PerformanceTier"]
            : media == "HDD"
                ? owner.Localization["CapacityTier"]
                : media == "SCM"
                    ? owner.Localization["DedicatedTier"]
                    : owner.Localization["StorageTier"];
    }
}
