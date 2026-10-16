using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinPool_App.Controls;

/// <summary>Shared default dimensions for the two editor property panels.</summary>
internal static class EditorFormMetrics
{
    public const double SidebarWidth = 320;
    public const double LabelWidth = 80;
    public const double ValueColumnWeight = 106;
    public const double MinimumValueWidth = 80;
    public const double PageMargin = 8;
    public const double CompactActionButtonWidth = 24;
    public const double UnitColumnWidth = 28;
    public const double MaximumButtonWidth = 40;

    public static void ConfigurePropertyPane(ScrollViewer pane, FrameworkElement content, ColumnDefinition column)
    {
        void Fit()
        {
            var width = column.Width.IsAbsolute ? column.Width.Value : pane.ViewportWidth;
            content.Width = Math.Max(1, width > 0 ? width : pane.ActualWidth > 0 ? pane.ActualWidth : SidebarWidth);
        }
        // Splitters update Width before measure. Reading ActualWidth only after
        // layout can retain the old content minimum while the pane shrinks.
        column.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, (_, _) => Fit());
        pane.SizeChanged += (_, _) => Fit();
        Fit();
    }

    public static void ConfigureColumns(Grid grid, FrameworkElement content)
    {
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new() { Width = new(LabelWidth) });
        grid.ColumnDefinitions.Add(new() { Width = new(CompactActionButtonWidth) });
        grid.ColumnDefinitions.Add(new() { Width = new(ValueColumnWeight), MinWidth = MinimumValueWidth });
        grid.ColumnDefinitions.Add(new() { Width = new(UnitColumnWidth) });
        grid.ColumnDefinitions.Add(new() { Width = new(MaximumButtonWidth) });
        grid.ColumnSpacing = 4;
        void Resize() => ResizeColumns(grid,
            (double.IsFinite(content.Width) ? content.Width : SidebarWidth) - 2 * (12 + 1));
        // Resize from the pane's finite width before measure: the previous pixel
        // columns otherwise keep the Grid's own ActualWidth from shrinking.
        content.RegisterPropertyChangedCallback(FrameworkElement.WidthProperty, (_, _) => Resize());
        Resize();
    }

    private static void ResizeColumns(Grid grid, double width)
    {
        var available = Math.Max(MinimumValueWidth, width - CompactActionButtonWidth
            - UnitColumnWidth - MaximumButtonWidth - 4 * grid.ColumnSpacing);
        var valueWidth = Math.Max(MinimumValueWidth, available * ValueColumnWeight / (LabelWidth + ValueColumnWeight));
        // Explicit calculated widths prevent long labels from changing a Grid's
        // star-column minimum and misaligning otherwise identical property cards.
        grid.ColumnDefinitions[0].Width = new(Math.Max(0, available - valueWidth));
        grid.ColumnDefinitions[2].Width = new(valueWidth);
    }
}
