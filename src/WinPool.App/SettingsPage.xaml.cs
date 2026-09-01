using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Ipc;
using DomainStorageLocationMode = WinPool.Domain.StorageLocationMode;

namespace WinPool_App;

public sealed partial class SettingsPage : Page
{
    private bool _ready;
    private bool _updatingMode;
    private bool _updatingDataLocation;
    private bool _updatingLanguage;
    private readonly AgentStartupRegistration _agentStartup = new();

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
        StartupAgentCheckBox.IsChecked = ViewModel.CurrentPreferences.StartAgentAtLogin;
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

        var requestedMode = (DomainStorageLocationMode)DataLocationOptions.SelectedIndex;
        var currentMode = (DomainStorageLocationMode)(int)StorageDataLocations.Mode;
        if (requestedMode == currentMode)
        {
            return;
        }

        _updatingDataLocation = true;
        DataLocationOptions.SelectedIndex = (int)currentMode;
        _updatingDataLocation = false;
        DataLocationOptions.IsEnabled = false;
        try
        {
            await SwitchDataLocationAsync(requestedMode);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {
            await RestartAgentAfterAbortedSwitchAsync();
            PublishDataLocationFailure(
                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn,
                exception.GetType().Name);
        }
        finally
        {
            DataLocationOptions.IsEnabled = true;
        }
    }

    private async Task SwitchDataLocationAsync(DomainStorageLocationMode requestedMode)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var warning = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = RequestedTheme,
            Title = zh ? "切换数据存储位置" : "Switch data storage location",
            Content = zh
                ? "切换会停止托盘 Agent 和后台监控。源数据会保留；复制与 SQLite 逻辑校验成功后才提交位置指针，随后 WinPool 会重启。"
                : "Switching stops the tray Agent and background monitoring. Source data is retained; the location pointer is committed only after copy and SQLite logical verification, then WinPool restarts.",
            PrimaryButtonText = zh ? "继续" : "Continue",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (ViewModel.AgentConnection is null)
        {
            PublishDataLocationFailure(zh, "agent-unavailable");
            return;
        }

        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var shutdown = await ViewModel.AgentConnection.SendAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.StorageLocationSwitch,
                CorrelationId.New()),
            shutdownTimeout.Token);
        if (shutdown.Value is ShutdownResponse response && !response.Result.Completed)
        {
            PublishDataLocationFailure(zh, "agent-shutdown-incomplete");
            return;
        }

        if (!await DataLocationSwitchRuntime.WaitForAgentExitAsync(shutdownTimeout.Token))
        {
            PublishDataLocationFailure(zh, "agent-exit-timeout");
            return;
        }

        using var agentExclusion = DataLocationSwitchRuntime.TryAcquireAgentMigrationExclusion();
        if (agentExclusion is null)
        {
            PublishDataLocationFailure(zh, "agent-restarted-during-handoff");
            return;
        }

        using var migrationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var manager = DataLocationSwitchRuntime.CreateManager();
        var planResult = await manager.PlanSwitchAsync(
            requestedMode,
            CorrelationId.New(),
            migrationTimeout.Token);
        if (!planResult.IsSuccess || planResult.Value is null)
        {
            agentExclusion.Release();
            await RestartAgentAfterAbortedSwitchAsync();
            PublishDataLocationFailure(zh, "migration-plan-failed");
            return;
        }

        var plan = planResult.Value;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = RequestedTheme,
            Title = zh ? "确认迁移数据" : "Confirm data migration",
            Content = zh
                ? $"将复制 {plan.FileCount:N0} 个文件（{FormatBytes(plan.TotalBytes)}）。\n\n源：{plan.SourceRoot}\n目标：{plan.TargetRoot}\n清单 SHA-256：{plan.SourceManifestSha256}"
                : $"Copy {plan.FileCount:N0} files ({FormatBytes(plan.TotalBytes)}).\n\nSource: {plan.SourceRoot}\nTarget: {plan.TargetRoot}\nManifest SHA-256: {plan.SourceManifestSha256}",
            PrimaryButtonText = zh ? "迁移并重启" : "Migrate and restart",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            agentExclusion.Release();
            await RestartAgentAfterAbortedSwitchAsync();
            return;
        }

        var applied = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            migrationTimeout.Token);
        if (!applied.IsSuccess || applied.Value is null)
        {
            agentExclusion.Release();
            await RestartAgentAfterAbortedSwitchAsync();
            PublishDataLocationFailure(zh, "migration-apply-failed");
            return;
        }

        agentExclusion.Release();
        if (!DataLocationSwitchRuntime.StartReplacementApplication())
        {
            await RestartAgentAfterAbortedSwitchAsync();
            ViewModel.NotificationService.PublishError(
                zh ? "重启 WinPool 失败" : "WinPool restart failed",
                zh
                    ? "数据位置已经安全提交，Agent 已按新位置恢复。请手动重启 WinPool，使主界面读取新位置。"
                    : "The data location was committed safely and the Agent resumed on it. Restart WinPool manually so the UI reads the new location.",
                "settings",
                $"datalocation-restart:{DateTimeOffset.UtcNow.Ticks}");
            return;
        }

        App.Window.Close();
    }

    private async Task RestartAgentAfterAbortedSwitchAsync()
    {
        if (ViewModel.AgentConnection is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (ViewModel.AgentConnection is NamedPipeAgentConnection namedPipeConnection)
            {
                await namedPipeConnection.ReconnectAsync(timeout.Token);
            }
            else
            {
                await ViewModel.AgentConnection.ConnectAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PublishDataLocationFailure(bool zh, string detail)
    {
        ViewModel.NotificationService.PublishError(
            zh ? "数据位置切换失败" : "Data location switch failed",
            zh
                ? $"未提交新的数据位置；请重试。诊断：{detail}"
                : $"The new data location was not committed; retry the operation. Diagnostic: {detail}",
            "settings",
            $"datalocation:{DateTimeOffset.UtcNow.Ticks}");
    }

    private void PublishPreferenceFailure(Exception exception)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        ViewModel.NotificationService.PublishError(
            zh ? "设置保存失败" : "Settings save failed",
            exception.Message,
            "settings",
            $"preference:{DateTimeOffset.UtcNow.Ticks}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    private async void ThemeOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeOptions.SelectedIndex < 0)
        {
            return;
        }

        try
        {
            var theme = (ThemePreference)ThemeOptions.SelectedIndex;
            await ViewModel.SetThemeAsync(theme);
            ((MainWindow)App.Window).ApplyTheme(theme);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            ThemeOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Theme;
            PublishPreferenceFailure(exception);
        }
    }

    private async void AccentOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AccentOptions.SelectedIndex < 0)
        {
            return;
        }

        try
        {
            var accent = (AccentColorPreference)AccentOptions.SelectedIndex;
            await ViewModel.SetAccentColorAsync(accent);
            ((MainWindow)App.Window).ApplyAccentColor(accent);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            AccentOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.AccentColor;
            PublishPreferenceFailure(exception);
        }
    }

    private async void LanguageOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingLanguage || LanguageOptions.SelectedIndex < 0)
        {
            return;
        }

        _updatingLanguage = true;
        try
        {
            var language = (LanguagePreference)LanguageOptions.SelectedIndex;
            await ViewModel.SetLanguageAsync(language);
            PopulateComboBoxes();
            ThemeOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Theme;
            AccentOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.AccentColor;
            LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
            UpdateText();
            ((MainWindow)App.Window).RefreshChrome();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
            PublishPreferenceFailure(exception);
        }
        finally
        {
            _updatingLanguage = false;
        }
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

        try
        {
            await ViewModel.SetCreateMsrOnInitializeAsync(MsrCheckBox.IsChecked == true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            MsrCheckBox.IsChecked = ViewModel.CurrentPreferences.CreateMsrOnInitialize;
            PublishPreferenceFailure(exception);
        }
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

        try
        {
            await ViewModel.SetShowHardwareIdsAsync(ShowHardwareIdsCheckBox.IsChecked == true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            ShowHardwareIdsCheckBox.IsChecked = ViewModel.CurrentPreferences.ShowHardwareIds;
            PublishPreferenceFailure(exception);
        }
    }

    private void WelcomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow mainWindow)
        {
            mainWindow.ShowWelcome();
        }
    }

    private async void CommunityButton_Click(object sender, RoutedEventArgs e)
    {
        const string groupUrl =
            "https://qm.qq.com/cgi-bin/qm/qr?k=iw0LxnFaHE8JdUr5z937pFuagFxOtFOo&jump_from=webapi&authKey=JvkcK/IIaFg5e1ymzhP41yxcAiTVURjvhrNtDziZZSGj3ZD2byZhqX2lj48L9jkT";
        try
        {
            Process.Start(new ProcessStartInfo(groupUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            await ShowMessageDialogAsync(
                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                    ? $"无法打开 QQ 群链接。群号：732019606\n{exception.Message}"
                    : $"Could not open the QQ group link. Group: 732019606\n{exception.Message}");
        }
    }

    private async void StartupAgentCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            var enabled = StartupAgentCheckBox.IsChecked == true;
            _agentStartup.SetEnabled(enabled);
            await ViewModel.SetStartAgentAtLoginAsync(enabled);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            StartupAgentCheckBox.IsChecked = ViewModel.CurrentPreferences.StartAgentAtLogin;
            await ShowMessageDialogAsync(exception.Message);
        }
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
        WelcomeButton.Content = l["OpenWelcome"];
        StartupAgentTitle.Text = l.EffectiveLanguage == LanguagePreference.ZhCn
            ? "开机启动"
            : "Startup";
        StartupAgentCheckBox.Content =
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "登录 Windows 时启动 WinPool 托盘（默认关闭）"
                : "Start the WinPool tray Agent when signing in to Windows (off by default)";
        DataLocationTitle.Text = l["DataLocation"];
        DataLocationPath.Text = StorageDataLocations.CurrentRoot;
        _updatingDataLocation = true;
        DataLocationOptions.ItemsSource = new[] { l["StandardLocation"], l["PortableLocation"] };
        DataLocationOptions.SelectedIndex = (int)StorageDataLocations.Mode;
        _updatingDataLocation = false;
        AboutProductNameLabel.Text = l["Product"];
        AboutProductNameValue.Text = ProductInformation.Name;
        AboutVersionLabel.Text = l["Version"];
        AboutVersionValue.Text = ProductInformation.Version;
        AboutProviderLabel.Text = l["Provider"];
        AboutWebsiteLabel.Text = l["Website"];
        AboutUpdateLabel.Text = l["Update"];
        AboutFeedbackLabel.Text = l["Feedback"];
        AboutCommunityLabel.Text = l["Community"];
        CommunityButtonText.Text = l.EffectiveLanguage == LanguagePreference.ZhCn
            ? "加入 QQ 群"
            : "Join QQ group";
        WebsiteButtonText.Text = l["VisitWebsite"];
        UpdateButtonText.Text = l["ViewUpdates"];
        FeedbackButtonText.Text = l["SendFeedback"];
        SyncExecutionMode();
    }

    private async Task ShowMessageDialogAsync(string content)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh ? "WinPool" : "WinPool",
            Content = content,
            CloseButtonText = zh ? "关闭" : "Close"
        };
        await dialog.ShowAsync();
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
