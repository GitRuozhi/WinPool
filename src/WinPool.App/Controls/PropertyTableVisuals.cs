using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinPool_App.Controls;

internal static class PropertyTableVisuals
{
    internal const double LabelColumnWidth = 96;
    internal const double ColumnGap = 8;
    internal const double RowHeight = 32;

    internal static Border CreateCell(
        UIElement child,
        Brush dividerBrush,
        double leftMargin = 0,
        double minHeight = RowHeight,
        object? tag = null) => new()
        {
            MinHeight = minHeight,
            Margin = new Thickness(leftMargin, 0, 0, 0),
            BorderBrush = dividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = child,
            Tag = tag
        };
}
