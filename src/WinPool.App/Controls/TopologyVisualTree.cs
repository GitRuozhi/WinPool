using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinPool.App.ViewModels;

namespace WinPool_App.Controls;

internal static class TopologyVisualTree
{
    public static TopologyNodeViewModel? FindOwnerViewModel(DependencyObject origin)
    {
        for (var current = VisualTreeHelper.GetParent(origin);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is TopologyNodeControl control)
            {
                return control.ViewModel;
            }
        }

        return null;
    }
}
