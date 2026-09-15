using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Infrastructure.Windows;
using WinPool_App.Controls;

namespace WinPool_App;

/// <summary>Read-only hardware report. Source matching and value selection stay outside the UI.</summary>
public sealed partial class HardwarePage : Page
{
    private const double LabelWidth = 142;
    private const double CellMinWidth = 170;
    private readonly TextBlock title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button refresh = new();
    private readonly Button export = new();
    private readonly StackPanel report = new() { Spacing = 20 };
    private WorkspaceViewModel viewModel = null!;
    private CancellationTokenSource? capture;
    private bool active;

    public HardwarePage()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(refresh, "HardwareRefresh");
        AutomationProperties.SetAutomationId(export, "HardwareExport");
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 10 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(refresh, 1); header.Children.Add(refresh);
        Grid.SetColumn(export, 2); header.Children.Add(export);
        root.Children.Add(header);
        Grid.SetRow(status, 1); root.Children.Add(status);
        var scroll = new ScrollViewer { Content = report, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        Content = root;
        refresh.Click += Refresh_Click;
        export.Click += Export_Click;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        viewModel = (WorkspaceViewModel)e.Parameter;
        active = true;
        viewModel.PropertyChanged += Changed;
        viewModel.WorkspaceSelectionChanged += SelectionChanged;
        viewModel.Localization.PropertyChanged += Changed;
        ActualThemeChanged += HardwarePage_ActualThemeChanged;
        Rebuild();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        capture?.Cancel();
        viewModel.PropertyChanged -= Changed;
        viewModel.WorkspaceSelectionChanged -= SelectionChanged;
        viewModel.Localization.PropertyChanged -= Changed;
        ActualThemeChanged -= HardwarePage_ActualThemeChanged;
        base.OnNavigatedFrom(e);
    }

    private void HardwarePage_ActualThemeChanged(FrameworkElement sender, object args) => Rebuild();

    private void SelectionChanged(object? sender, EventArgs e) => Rebuild();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, viewModel.Localization) || e.PropertyName is nameof(WorkspaceViewModel.SelectedSystem)
            or nameof(WorkspaceViewModel.Snapshot) or nameof(WorkspaceViewModel.IsScanning)) Rebuild();
    }

    private void Rebuild()
    {
        title.Text = viewModel.Localization["HardwareReadOnly"];
        refresh.Content = capture is null ? viewModel.Localization["HardwareRefresh"] : viewModel.Localization["Cancel"];
        export.Content = viewModel.Localization["Export"];
        refresh.IsEnabled = capture is not null || viewModel.SelectedSystem.IsLocal && !viewModel.IsScanning;
        export.IsEnabled = capture is null;
        var unified = viewModel.SelectedSystem.Unified;
        status.Text = unified is null ? viewModel.Localization["HardwareEmpty"] : string.Join(" · ", unified.Collections.Select(x =>
            $"{(x.Purpose == CollectionPurpose.Storage ? viewModel.Localization["Manage"] : viewModel.Localization["Hardware"])}: {x.CompletedAt.LocalDateTime:G} ({viewModel.Localization["Source" + x.State]})"));
        report.Children.Clear();
        var categories = HardwareReportProjector.Project(viewModel.SelectedSystem, viewModel.Localization.IsChinese);
        if (categories.Count == 0)
        {
            report.Children.Add(new TextBlock { Text = viewModel.Localization["HardwareEmpty"], TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var category in categories) report.Children.Add(BuildCategory(category));
    }

    private FrameworkElement BuildCategory(HardwareReportCategory category)
    {
        var panel = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = category.Name, FontSize = 18, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(heading);
        foreach (var section in category.Sections)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(LabelWidth) });
            var count = Math.Max(1, section.Rows.Select(x => x.Cells.Count).DefaultIfEmpty(1).Max());
            for (var column = 0; column < count; column++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(CellMinWidth) });
            for (var rowIndex = 0; rowIndex < section.Rows.Count; rowIndex++)
            {
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var row = section.Rows[rowIndex];
                var label = BorderCell(new TextBlock { Text = row.Label, Padding = new Thickness(10, 6, 10, 6),
                    VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap });
                Grid.SetRow(label, rowIndex); grid.Children.Add(label);
                for (var column = 0; column < row.Cells.Count; column++)
                {
                    var cell = row.Cells[column];
                    var button = new Button { Content = new TextBlock { Text = cell.Value, TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true }, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0), Padding = new Thickness(10, 6, 10, 6),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                        Tag = (category.Name, row.Label, cell) };
                    AutomationProperties.SetName(button, $"{category.Name}, {row.Label}, {cell.Value}");
                    ToolTipService.SetToolTip(button, viewModel.Localization.IsChinese ? "查看来源与原值" : "View source and raw value");
                    button.Click += Detail_Click;
                    var border = BorderCell(button);
                    Grid.SetRow(border, rowIndex); Grid.SetColumn(border, column + 1); grid.Children.Add(border);
                }
            }
            panel.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        return panel;
    }

    private static Border BorderCell(UIElement child) => PropertyTableVisuals.CreateCell(
        child,
        (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        minHeight: 34);

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string, HardwareReportCell> data }) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"{data.Item1} · {data.Item2}",
            CloseButtonText = viewModel.Localization["Close"], Content = new ScrollViewer { MaxHeight = 520,
                Content = new TextBlock { Text = data.Item3.Details, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } };
        await dialog.ShowAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (capture is not null) { capture.Cancel(); return; }
        if (!viewModel.SelectedSystem.IsLocal) return;
        capture = new CancellationTokenSource(); Rebuild();
        try { await viewModel.RefreshHardwareAsync(capture.Token); }
        catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
        finally { capture.Dispose(); capture = null; if (active) Rebuild(); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        export.IsEnabled = false;
        try
        {
            var targetId = viewModel.SelectedSystem.Id;
            var path = await viewModel.ExportActiveSystemAsync();
            if (path is not null && active && viewModel.SelectedSystem.Id == targetId) status.Text = viewModel.Localization["Exported"];
        }
        finally { if (active) export.IsEnabled = true; }
    }
}
