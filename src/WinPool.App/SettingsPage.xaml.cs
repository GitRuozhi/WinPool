using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Core;
using WinPool.Infrastructure.Windows;

namespace WinPool_App;

public sealed partial class SettingsPage : Page
{
    private bool _ready;
    private bool _updatingMode;
    private bool _updatingDataLocation;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        PopulateComboBoxes();
        ThemeOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Theme;
        AccentOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.AccentColor;
        LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
        MsrCheckBox.IsChecked = ViewModel.CurrentPreferences.CreateMsrOnInitialize;
        ShowHardwareIdsCheckBox.IsChecked = ViewModel.CurrentPreferences.ShowHardwareIds;
        WelcomeCheckBox.IsChecked = ViewModel.CurrentPreferences.ShowWelcomeAtStart;
        _updatingDataLocation = true;
        DataLocationOptions.SelectedIndex = (int)StorageDataLocations.Mode;
        _updatingDataLocation = false;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateText();
        SyncExecutionMode();
        _ready = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnNavigatedFrom(e);
    }

    private void PopulateComboBoxes()
    {
        var l = ViewModel.Localization;
        ThemeOptions.ItemsSource = new[] { l["SystemTheme"], l["Light"], l["Dark"] };
        AccentOptions.ItemsSource = new[]
        {
            l["SystemAccent"], l["Blue"], l["Cyan"], l["Green"], l["Purple"], l["Orange"], l["Red"]
        };
        LanguageOptions.ItemsSource = new[] { l["SystemLanguage"], l["Chinese"], l["English"] };
        DataLocationOptions.ItemsSource = new[] { l["StandardLocation"], l["PortableLocation"] };
    }

    private async void DataLocationOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingDataLocation || DataLocationOptions.SelectedIndex < 0)
        {
            return;
        }

        var l = ViewModel.Localization;
        var mode = (StorageLocationMode)DataLocationOptions.SelectedIndex;
        var result = await StorageDataLocations.SetModeAsync(mode);
        if (!result.Success)
        {
            _updatingDataLocation = true;
            DataLocationOptions.SelectedIndex = (int)StorageDataLocations.Mode;
            _updatingDataLocation = false;
            ViewModel.NotificationService.PublishError(
                l["Error"],
                l["DataLocationFailed"],
                "settings",
                $"datalocation:{DateTimeOffset.UtcNow.Ticks}");
            return;
        }

        if (mode == StorageDataLocations.Mode)
        {
            DataLocationPath.Text = StorageDataLocations.CurrentRoot;
            ViewModel.NotificationService.PublishInfo(
                l["DataLocation"],
                l["DataLocationSwitched"],
                "settings",
                $"datalocation:{DateTimeOffset.UtcNow.Ticks}");
        }
    }
    private async void ThemeOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeOptions.SelectedIndex < 0)
        {
            return;
        }

        var theme = (ThemePreference)ThemeOptions.SelectedIndex;
        await ViewModel.SetThemeAsync(theme);
        ((MainWindow)App.Window).ApplyTheme(theme);
    }

    private async void AccentOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AccentOptions.SelectedIndex < 0)
        {
            return;
        }

        var accent = (AccentColorPreference)AccentOptions.SelectedIndex;
        await ViewModel.SetAccentColorAsync(accent);
        ((MainWindow)App.Window).ApplyAccentColor(accent);
    }

    private async void LanguageOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageOptions.SelectedIndex < 0)
        {
            return;
        }

        var language = (LanguagePreference)LanguageOptions.SelectedIndex;
        await ViewModel.SetLanguageAsync(language);
        PopulateComboBoxes();
        ThemeOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Theme;
        AccentOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.AccentColor;
        LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
        UpdateText();
        ((MainWindow)App.Window).RefreshChrome();
    }

    private async void SettingsExecutionModeSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingMode)
        {
            return;
        }

        var requestedMode = SettingsExecutionModeSwitch.IsChecked == true
            ? ExecutionMode.Real
            : ExecutionMode.Simulation;
        await ((MainWindow)App.Window).RequestExecutionModeAsync(requestedMode);
        SyncExecutionMode();
        ((MainWindow)App.Window).RefreshChrome();
    }

    private async void MsrCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }
        await ViewModel.SetCreateMsrOnInitializeAsync(MsrCheckBox.IsChecked == true);
    }

    private async void ShowHardwareIdsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        if (ShowHardwareIdsCheckBox.IsChecked == true)
        {
            var l = ViewModel.Localization;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
                Title = l["PrivacyWarningTitle"],
                Content = l["PrivacyWarningMessage"],
                PrimaryButtonText = l["Confirm"],
                CloseButtonText = l["Cancel"],
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                ShowHardwareIdsCheckBox.IsChecked = false;
                return;
            }
        }

        await ViewModel.SetShowHardwareIdsAsync(ShowHardwareIdsCheckBox.IsChecked == true);
    }

    private async void WelcomeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }
        await ViewModel.SetShowWelcomeAtStartAsync(WelcomeCheckBox.IsChecked == true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.IsRealMode))
        {
            SyncExecutionMode();
        }
    }

    private void SyncExecutionMode()
    {
        _updatingMode = true;
        SettingsExecutionModeSwitch.IsEnabled = true;
        SettingsExecutionModeSwitch.IsChecked = ViewModel.IsRealMode;
        ToolTipService.SetToolTip(
            SettingsExecutionModeSwitch,
            ViewModel.CanUseRealMode ? ViewModel.Localization["ExecutionMode"] : ViewModel.Localization["AdminRequired"]);
        SettingsExecutionModeSwitch.SetValue(
            AutomationProperties.NameProperty,
            ViewModel.Localization["LocalRealOperations"]);
        _updatingMode = false;
    }

    private void UpdateText()
    {
        var l = ViewModel.Localization;
        ThemeTitle.Text = l["Appearance"];
        AccentTitle.Text = l["AccentColor"];
        LanguageTitle.Text = l["Language"];
        ExecutionTitle.Text = l["ExecutionMode"];
        SettingsExecutionModeSwitch.Content = l["LocalRealOperations"];
        MsrTitle.Text = l["InitializeDisk"];
        MsrCheckBox.Content = l["CreateMsrOnInitialize"];
        PrivacyTitle.Text = l["Privacy"];
        ShowHardwareIdsCheckBox.Content = l["ShowHardwareIds"];
        WelcomeTitle.Text = l["Welcome"];
        WelcomeCheckBox.Content = l["ShowWelcomeAtStart"];
        DataLocationTitle.Text = l["DataLocation"];
        DataLocationPath.Text = StorageDataLocations.CurrentRoot;
        _updatingDataLocation = true;
        DataLocationOptions.ItemsSource = new[] { l["StandardLocation"], l["PortableLocation"] };
        DataLocationOptions.SelectedIndex = (int)StorageDataLocations.Mode;
        _updatingDataLocation = false;
        AboutTitle.Text = l["About"];
        AboutProductNameLabel.Text = l["Product"];
        AboutProductNameValue.Text = ProductInformation.Name;
        AboutVersionLabel.Text = l["Version"];
        AboutVersionValue.Text = ProductInformation.Version;
        AboutProviderLabel.Text = l["Provider"];
        AboutWebsiteLabel.Text = l["Website"];
        AboutUpdateLabel.Text = l["Update"];
        AboutFeedbackLabel.Text = l["Feedback"];
        AboutCommunityLabel.Text = l["Community"];
        AboutCommunityValue.Text = l["CommunityPending"];
        WebsiteButtonText.Text = l["VisitWebsite"];
        UpdateButtonText.Text = l["ViewUpdates"];
        FeedbackButtonText.Text = l["SendFeedback"];
        SyncExecutionMode();
    }

    private async void WebsiteLink_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync(ProductInformation.WebsiteUri);

    private async void UpdateLink_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync(ProductInformation.UpdateUri);

    private async void FeedbackLink_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync(ProductInformation.FeedbackUri);

    private async Task OpenAsync(Uri uri)
    {
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(uri))
            {
                return;
            }
        }
        catch
        {
        }

        ViewModel.NotificationService.PublishError(
            ViewModel.Localization["Error"],
            ViewModel.Localization["OpenUpdateFailed"],
            "updates",
            $"updates:{DateTimeOffset.UtcNow.Ticks}");
    }
}
