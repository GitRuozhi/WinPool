using Microsoft.UI.Xaml;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool.App.Services;

public static class TopologyLayoutMapper
{
    public static TopologyLayoutInput FromViewModel(TopologyNodeViewModel viewModel) =>
        new(
            viewModel.HeaderVisibility == Visibility.Visible,
            viewModel.IsExpanded,
            viewModel.ChildrenLayout,
            viewModel.Children.Select(FromViewModel).ToList());
}
