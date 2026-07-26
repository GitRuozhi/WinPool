using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WinPool.App.ViewModels;
using WinPool.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinPool_App;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        ViewModel.WorkspaceSelectionChanged += ViewModel_WorkspaceSelectionChanged;
        Bindings.Update();
        RefreshLocalizedText();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.WorkspaceSelectionChanged -= ViewModel_WorkspaceSelectionChanged;
        ViewModel.TopologyHorizontalOffset = TopologyScrollViewer.HorizontalOffset;
        ViewModel.TopologyVerticalOffset = TopologyScrollViewer.VerticalOffset;
        base.OnNavigatedFrom(e);
    }

    private async void MainPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.Snapshot.ScannedAt == DateTimeOffset.MinValue && !ViewModel.IsScanning)
        {
            await ViewModel.ScanAsync();
        }

        DispatcherQueue.TryEnqueue(() =>
            TopologyScrollViewer.ChangeView(
                ViewModel.TopologyHorizontalOffset,
                ViewModel.TopologyVerticalOffset,
                null,
                disableAnimation: true));
    }

    private async void RescanButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await ViewModel.ScanAsync();

    private void CopySummaryButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        CopyText(ViewModel.CreateSelectedSummary());

    private void CopyIdButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        CopyText(ViewModel.ResolveDetailUnit()?.StableId ?? string.Empty);

    private async void ExportButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var result = await ViewModel.ExportService.ExportAsync(
                ViewModel.ActiveSnapshot,
                ViewModel.ResolveDetailUnit());
            if (result is not null)
            {
                ViewModel.StatusMessage = ViewModel.Localization["Exported"];
            }
        }
        catch (Exception ex)
        {
            PublishOperationError(ex);
        }
    }

    private void OpenPartitionButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var unit = ViewModel.ResolveDetailUnit();
        var partition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(x => x.StableId == unit?.StableId);
        if (ViewModel.IsUsingSimulatedInventory
            || partition is null
            || !Directory.Exists(partition.Path))
        {
            ViewModel.NotificationService.PublishWarning(
                ViewModel.Localization["Warning"],
                ViewModel.Localization["InvalidPartitionPath"],
                "open-partition",
                $"open-partition:{DateTimeOffset.UtcNow.Ticks}");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{partition.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PublishOperationError(ex);
        }
    }

    private void RelatedButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ViewModel.NavigateToPrimaryRelatedTarget();

    private void CopyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(value);
            Clipboard.SetContent(package);
            ViewModel.StatusMessage = ViewModel.Localization["Copied"];
        }
        catch (Exception ex)
        {
            PublishOperationError(ex);
        }
    }

    private void PublishOperationError(Exception exception) =>
        ViewModel.NotificationService.PublishError(
            ViewModel.Localization["Error"],
            $"{ViewModel.Localization["OperationFailed"]} {exception.Message}".Trim(),
            "workspace-operation",
            $"workspace-operation:{DateTimeOffset.UtcNow.Ticks}");

    private void RefreshLocalizedText()
    {
        var l = ViewModel.Localization;
        RescanButtonText.Text = l["Rescan"];
        CopySummaryButtonText.Text = l["CopySummary"];
        CopyIdButtonText.Text = l["CopyId"];
        ExportButtonText.Text = l["Export"];
        OpenPartitionButtonText.Text = l["Open"];
        RelatedButtonText.Text = l["ViewRelated"];
    }

    private void TopologyScrollViewer_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        // Constrain the recursive block layout to the viewport. Child flow panels can
        // then wrap in-place instead of manufacturing a very wide virtual canvas.
        var width = Math.Max(320, e.NewSize.Width - 20);
        TopologySystemsControl.Width = width;
        ViewModel.UpdateTopologyViewportWidth(width);
    }

    private void ViewModel_WorkspaceSelectionChanged(object? sender, EventArgs e)
    {
        if (ViewModel.SelectedWorkspaceItem is not null)
        {
            ObjectList.ScrollIntoView(ViewModel.SelectedWorkspaceItem);
        }
    }
}
