using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

public sealed partial class StorageStructurePage
{
    private void VirtualDiskAction_Click(object sender, RoutedEventArgs e)
    {
        if (_virtualDiskActionDeletes) DeleteVdisk_Click(sender, e);
        else CreateVdisk_Click(sender, e);
    }

    private MenuFlyout? CreateTopologyContextMenu(TopologyNodeViewModel node)
    {
        // Selection can be refused while a capacity field is invalid. Never
        // offer actions for the previously selected object in that case.
        if (!IsTopologyNodeSelected(node)) return null;
        var menu = new MenuFlyout();
        AddAction(PendingActionsButton, PendingActionsButtonLabel.Text, "\uE14C", PendingActions_Click);
        AddAction(UndoButton, ViewModel.Localization["Undo"], "\uE7A7", Undo_Click);
        AddAction(RedoButton, ViewModel.Localization["Redo"], "\uE7A6", Redo_Click);
        AddAction(DiscardAllButton, ViewModel.Localization["DiscardAll"], "\uE711", DiscardAll_Click);
        AddAction(ApplyAllButton, ViewModel.Localization["ApplyAll"], "\uE73E", ApplyAll_Click);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddAction(DissolveButton, DissolveButtonLabel.Text, "\uE74D", Dissolve_Click);
        AddAction(CreateVdiskButton, CreateVdiskButtonLabel.Text,
            _virtualDiskActionDeletes ? "\uE74D" : "\uE710", VirtualDiskAction_Click);
        AddAction(RetireButton, RetireButtonLabel.Text, "\uE7E8", Retire_Click);
        AddAction(HotSpareButton, HotSpareButtonLabel.Text, "\uE72C", HotSpare_Click);

        if (node.Unit.Kind == StorageUnitKind.PhysicalDisk)
        {
            var diskId = node.Unit.StableId;
            var currentPoolId = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId)?.PoolStableId;
            var destinations = _working.StoragePools
                .Where(pool => !string.Equals(pool.StableId, currentPoolId, StringComparison.OrdinalIgnoreCase)
                    && CanDropDisk(diskId, pool.StableId)).ToArray();
            if (destinations.Length > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var pool in destinations)
            {
                var targetId = pool.StableId;
                var item = new MenuFlyoutItem
                {
                    Text = Text($"移动到{pool.FriendlyName}池", $"Move to pool {pool.FriendlyName}"),
                    Icon = new FontIcon { Glyph = "\uE8DE" }
                };
                item.Click += (_, _) => OnDiskDropped(diskId, targetId);
                menu.Items.Add(item);
            }
        }
        return menu;

        void AddAction(Button source, string name, string glyph, RoutedEventHandler execute)
        {
            var item = new MenuFlyoutItem
            {
                Text = name,
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = source.IsEnabled
            };
            item.Click += (sender, args) =>
            {
                if (source.IsEnabled) execute(sender, args);
            };
            menu.Items.Add(item);
        }
    }
}
