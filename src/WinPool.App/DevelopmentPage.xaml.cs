using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Core;

namespace WinPool_App;

public sealed partial class DevelopmentPage : Page
{
    private WorkspaceViewModel ViewModel { get; set; } = null!;
    private readonly ObservableCollection<string> _lines = [];

    public DevelopmentPage()
    {
        InitializeComponent();
        LogItems.ItemsSource = _lines;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        DevelopmentIntro.Text = ViewModel.Localization["DevelopmentIntro"];
        ClearButton.Content = zh ? "清空" : "Clear";
        Rebuild();
        ((INotifyCollectionChanged)ViewModel.CommandLog.Entries).CollectionChanged += Entries_CollectionChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ((INotifyCollectionChanged)ViewModel.CommandLog.Entries).CollectionChanged -= Entries_CollectionChanged;
        base.OnNavigatedFrom(e);
    }

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild();
        DispatcherQueue.TryEnqueue(() =>
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, disableAnimation: true));
    }

    private void Rebuild()
    {
        _lines.Clear();
        foreach (var entry in ViewModel.CommandLog.Entries)
        {
            var tag = entry.Simulated ? "[SIM]" : "[REAL]";
            _lines.Add($"{entry.At:HH:mm:ss} {tag} [{entry.Source}] {entry.Command}");
            if (!string.IsNullOrWhiteSpace(entry.Output))
            {
                _lines.Add($"    → {entry.Output}");
            }
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CommandLog.Clear();
    }
}
