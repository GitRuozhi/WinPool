using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using WinPool.Core;

namespace WinPool.App.ViewModels;

public sealed partial class TopologyNodeViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _owner;

    private readonly StorageSnapshot _snapshot;

    private readonly string _occurrenceKey;

    public TopologyNodeViewModel(
        TopologyNode node,
        WorkspaceViewModel owner,
        StorageSnapshot snapshot,
        string occurrenceKey = "root")
    {
        _owner = owner;
        _snapshot = snapshot;
        _occurrenceKey = occurrenceKey;
        Unit = node.Unit with { DisplayName = LocalizeName(node.Unit, owner) };
        Summary = LocalizeSummary(node, owner, snapshot);
        TypeLabel = LocalizeType(node.Unit, owner, snapshot);
        IsReference = node.IsReference;
        IsSelectable = node.IsSelectable;
        ChildrenLayout = node.ChildrenLayout;
        LayoutWeight = node.LayoutWeight;
        _isExpanded = owner.GetExpandedState(_occurrenceKey, node.IsExpanded);
        Children = node.Children
            .Select((child, index) =>
                new TopologyNodeViewModel(
                    child,
                    owner,
                    snapshot,
                    $"{_occurrenceKey}/{index}:{child.Unit.Kind}"))
            .ToList();
        (BadgeText, BadgeIsWarning) = ComputeBadge(owner, snapshot, node.Unit);
    }

    public string BadgeText { get; }

    public bool BadgeIsWarning { get; }

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
                if (physical is not null)
                {
                    if (StorageFindingInspector.IsUnhealthy(physical.HealthStatus, physical.OperationalStatus))
                    {
                        return (owner.Localization["Unhealthy"], true);
                    }
                    var backingOsDisk = snapshot.OsDisks.FirstOrDefault(
                        x => x.PhysicalDiskStableId == physical.StableId);
                    if (backingOsDisk is { IsSystem: true } or { IsBoot: true })
                    {
                        return (owner.Localization["SystemDisk"], false);
                    }
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
                if (partition is not null)
                {
                    if (StorageFindingInspector.IsUnhealthy(partition.HealthStatus, partition.OperationalStatus))
                    {
                        return (owner.Localization["Unhealthy"], true);
                    }
                    if (partition.IsBoot || partition.IsSystem)
                    {
                        return (owner.Localization["SystemDisk"], false);
                    }
                }
                break;
            case StorageUnitKind.OsDisk:
                var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == unit.StableId);
                if (osDisk is { IsSystem: true } or { IsBoot: true })
                {
                    return (owner.Localization["SystemDisk"], false);
                }
                break;
        }
        return (string.Empty, false);
    }

    public StorageUnitRef Unit { get; }

    public string Summary { get; }

    public string TypeLabel { get; }

    public IReadOnlyList<TopologyNodeViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsReference { get; }

    public bool IsSelectable { get; }

    public TopologyChildrenLayout ChildrenLayout { get; }

    public int LayoutWeight { get; }

    public bool IsSelected =>
        ReferenceEquals(_owner.ActiveSnapshot, _snapshot)
        && _owner.SelectedTopologyStableId == Unit.StableId;

    public int HeaderRow => 0;

    public int ChildrenRow => 1;

    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string ExpandAutomationName =>
        IsExpanded ? $"Collapse {Unit.DisplayName}" : $"Expand {Unit.DisplayName}";

    public Visibility ExpandButtonVisibility =>
        HasChildren ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HeaderVisibility =>
        Unit.Kind == StorageUnitKind.DirectDiskGroup ? Visibility.Collapsed : Visibility.Visible;

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
        StorageUnitKind.NetworkDiskGroup or StorageUnitKind.OtherDiskGroup or StorageUnitKind.DirectDiskGroup => double.NaN,
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
        if (IsSelectable)
        {
            _owner.SelectTopologyUnit(Unit, _snapshot);
        }
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (HasChildren)
        {
            IsExpanded = !IsExpanded;
        }
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

    public bool ExpandPathTo(string stableId)
    {
        if (Unit.StableId.Equals(stableId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var child in Children)
        {
            if (!child.ExpandPathTo(stableId))
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
            _ when unit.Kind == StorageUnitKind.NetworkDiskGroup => owner.Localization["Network"],
            _ when unit.Kind == StorageUnitKind.OtherDiskGroup => owner.Localization["Other"],
            _ => unit.DisplayName
        };

    private static string LocalizeSummary(
        TopologyNode node,
        WorkspaceViewModel owner,
        StorageSnapshot snapshot)
    {
        if (node.Unit.Kind == StorageUnitKind.System)
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
                $"{snapshot.NetworkDisks.Count} {owner.Localization["NetworkDisk"]}",
                TopologyProjector.FormatBytes(physical.Sum(x => x.Size)));
        }

        if (node.Unit.Kind == StorageUnitKind.StoragePool)
        {
            var pool = snapshot.StoragePools.First(x => x.StableId == node.Unit.StableId);
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

        if (node.Unit.Kind == StorageUnitKind.StorageTier)
        {
            var tier = snapshot.StorageTiers.First(x => x.StableId == node.Unit.StableId);
            var members = snapshot.PhysicalDisks
                .Where(x => tier.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return TopologyProjector.JoinSummary(
                $"{members.Count} {owner.Localization["PhysicalDisk"]}",
                TopologyProjector.FormatBytes(members.Sum(x => x.Size)));
        }

        if (node.Unit.Kind == StorageUnitKind.PhysicalDisk)
        {
            var disk = snapshot.PhysicalDisks.First(x => x.StableId == node.Unit.StableId);
            var media = disk.MediaType is "HDD" or "SSD" or "SCM" ? disk.MediaType : owner.Localization["Unknown"];
            return TopologyProjector.JoinSummary(media, TopologyProjector.FormatBytes(disk.Size));
        }

        if (node.Unit.Kind == StorageUnitKind.VirtualDisk)
        {
            var disk = snapshot.VirtualDisks.First(x => x.StableId == node.Unit.StableId);
            return TopologyProjector.JoinSummary(
                disk.TierStableIds.Count > 0 ? "Tiered" : "Virtual",
                TopologyProjector.FormatBytes(disk.Size));
        }

        if (node.Unit.Kind == StorageUnitKind.NetworkDisk)
        {
            var disk = snapshot.NetworkDisks.First(x => x.StableId == node.Unit.StableId);
            return TopologyProjector.JoinSummary("Network", TopologyProjector.FormatBytes(disk.Size));
        }

        if (node.Unit.Kind == StorageUnitKind.NetworkDiskGroup)
        {
            return TopologyProjector.JoinSummary(
                $"{snapshot.NetworkDisks.Count} {owner.Localization["NetworkDisk"]}",
                TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size)));
        }

        if (node.Unit.Kind == StorageUnitKind.OtherDiskGroup)
        {
            var otherDisks = TopologyProjector.GetOtherOsDisks(snapshot);
            var otherIds = otherDisks.Select(x => x.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return TopologyProjector.JoinSummary(
                $"{otherDisks.Count} {owner.Localization["OtherDisk"]}",
                $"{snapshot.Partitions.Count(x => x.OsDiskStableId is not null && otherIds.Contains(x.OsDiskStableId))} {owner.Localization["Partition"]}",
                TopologyProjector.FormatBytes(otherDisks.Sum(x => x.Size)));
        }

        if (node.Unit.Kind == StorageUnitKind.Partition)
        {
            var partition = snapshot.Partitions.First(x => x.StableId == node.Unit.StableId);
            return TopologyProjector.JoinSummary(
                string.IsNullOrWhiteSpace(partition.FileSystem) ? owner.Localization["Unknown"] : partition.FileSystem,
                TopologyProjector.FormatBytes(partition.Size));
        }

        return node.Summary.Replace(" · ", "  ", StringComparison.Ordinal);
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
            StorageUnitKind.Partition => owner.PartitionTypeName(
                snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId)?.Type ?? "Unknown"),
            StorageUnitKind.OsDisk => owner.Localization["OtherDisk"],
            StorageUnitKind.NetworkDiskGroup => owner.Localization["NetworkStorageGroup"],
            StorageUnitKind.OtherDiskGroup => owner.Localization["OtherStorageGroup"],
            _ => unit.Kind.ToString()
        };

    private static string SnapshotPoolType(StorageUnitRef unit, WorkspaceViewModel owner, StorageSnapshot snapshot) =>
        snapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId)?.IsPrimordial == true
            ? owner.Localization["OriginalPool"]
            : owner.Localization["StoragePool"];

    private static string SnapshotTierType(StorageUnitRef unit, WorkspaceViewModel owner, StorageSnapshot snapshot)
    {
        var media = snapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId)?.MediaType;
        return media is "SSD" or "SCM"
            ? owner.Localization["PerformanceTier"]
            : media == "HDD"
                ? owner.Localization["CapacityTier"]
                : owner.Localization["StorageTier"];
    }
}
