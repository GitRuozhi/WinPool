using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Core;

namespace WinPool_App;

public sealed partial class SettingsPage : Page
{
    private bool _ready;
    private bool _updatingMode;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        ThemeOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Theme;
        AccentOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.AccentColor;
        LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
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
        UpdateText();
        ((MainWindow)App.Window).RefreshChrome();
    }

    private async void SettingsExecutionModeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingMode)
        {
            return;
        }

        var requestedMode = SettingsExecutionModeSwitch.IsOn
            ? ExecutionMode.Real
            : ExecutionMode.Simulation;
        await ((MainWindow)App.Window).RequestExecutionModeAsync(requestedMode);
        SyncExecutionMode();
        ((MainWindow)App.Window).RefreshChrome();
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
        SettingsExecutionModeSwitch.IsOn = ViewModel.IsRealMode;
        SettingsSimulationLabel.Opacity = ViewModel.IsRealMode ? 0.56 : 1;
        SettingsRealLabel.Opacity = ViewModel.IsRealMode ? 1 : 0.56;
        ToolTipService.SetToolTip(
            SettingsExecutionModeSwitch,
            ViewModel.CanUseRealMode ? ViewModel.Localization["ExecutionMode"] : ViewModel.Localization["AdminRequired"]);
        SettingsExecutionModeSwitch.SetValue(
            AutomationProperties.NameProperty,
            $"{ViewModel.Localization["Simulation"]} / {ViewModel.Localization["Real"]}");
        _updatingMode = false;
    }

    private void UpdateText()
    {
        var l = ViewModel.Localization;
        SettingsTitle.Text = l["Settings"];
        SettingsDescription.Text = l["SettingsDescription"];
        ThemeTitle.Text = l["Appearance"];
        ThemeSubtitle.Text = l.Language == LanguagePreference.ZhCn
            ? "跟随系统支持高对比度和系统主题变化。"
            : "System mode follows Windows theme changes and high contrast.";
        ThemeOptions.Items[0] = l["SystemTheme"];
        ThemeOptions.Items[1] = l["Light"];
        ThemeOptions.Items[2] = l["Dark"];
        AccentTitle.Text = l["AccentColor"];
        AccentSubtitle.Text = l["AccentDescription"];
        AccentOptions.Items[0] = l["SystemAccent"];
        AccentOptions.Items[1] = l["Blue"];
        AccentOptions.Items[2] = l["Cyan"];
        AccentOptions.Items[3] = l["Green"];
        AccentOptions.Items[4] = l["Purple"];
        AccentOptions.Items[5] = l["Orange"];
        AccentOptions.Items[6] = l["Red"];
        LanguageTitle.Text = l["Language"];
        LanguageSubtitle.Text = l.Language == LanguagePreference.ZhCn
            ? "磁盘型号、卷标和池名称保持原文。"
            : "Disk models, volume labels, and pool names remain unchanged.";
        LanguageOptions.Items[0] = l["Chinese"];
        LanguageOptions.Items[1] = l["English"];
        ExecutionTitle.Text = l["ExecutionMode"];
        ExecutionSubtitle.Text = l["ExecutionDescription"];
        SettingsSimulationLabel.Text = l["SimulationShort"];
        SettingsRealLabel.Text = l["RealShort"];
        AboutTitle.Text = l["About"];
        AboutSubtitle.Text = l["AboutDescription"];
        AboutProductNameLabel.Text = l["ProductName"];
        AboutProductNameValue.Text = ProductInformation.Name;
        AboutVersionLabel.Text = l["CurrentVersion"];
        AboutVersionValue.Text = ProductInformation.Version;
        UpdateTitle.Text = l["Update"];
        UpdateSubtitle.Text = l["UpdateDescription"];
        UpdateVersionLabel.Text = l["CurrentVersion"];
        UpdateVersionValue.Text = ProductInformation.Version;
        UpdateSourceLabel.Text = l["UpdateSource"];
        UpdateModeLabel.Text = l["UpdateMethod"];
        UpdateModeValue.Text = l["ExternalUpdate"];
        ViewUpdatesButtonText.Text = l["ViewUpdates"];
        ViewUpdatesButton.SetValue(AutomationProperties.NameProperty, l["ViewUpdates"]);
        SyncExecutionMode();
    }

    private async void ViewUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(ProductInformation.UpdateUri))
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
