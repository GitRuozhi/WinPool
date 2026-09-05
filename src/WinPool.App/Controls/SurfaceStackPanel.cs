using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App.Controls;

/// <summary>
/// Vertical stack adapter for topology children. When its owner view model
/// is a layout root it runs the unified engine once for the whole surface
/// and cascades the resulting slot plan; otherwise it is a plain vertical
/// stack identical to the StackPanel it replaces.
/// </summary>
public sealed class SurfaceStackPanel : Panel
{
    private const double FallbackMeasureWidth = 1200;

    public double Spacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = TopologyVisualTree.FindOwnerViewModel(this);
        if (owner is { IsLayoutRoot: true }
            && owner.IsExpanded
            && owner.Children.Count > 0
            && owner.Children.Count == Children.Count)
        {
            var width = !double.IsInfinity(availableSize.Width) && availableSize.Width > 1
                ? Math.Max(1, availableSize.Width)
                : Math.Max(1, owner.HostViewportWidth);
            var result = TopologyLayoutEngine.Layout(
                TopologyLayoutMapper.FromViewModel(owner),
                width);
            owner.ApplyLayout(result);
        }

        var desiredHeight = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            desiredHeight += child.DesiredSize.Height + Spacing;
        }

        if (Children.Count > 0)
        {
            desiredHeight -= Spacing;
        }

        var measuredWidth = double.IsInfinity(availableSize.Width)
            ? FallbackMeasureWidth
            : Math.Max(0, availableSize.Width);
        return new Size(measuredWidth, Math.Max(0, desiredHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var child in Children)
        {
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, finalSize.Width, height));
            y += height + Spacing;
        }

        return finalSize;
    }
}
