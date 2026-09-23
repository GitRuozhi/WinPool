using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
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
    private bool _updatingDeveloperMode;
    private bool _updatingMsr;
    private bool _updatingStartup;
    private bool _updatingPartitionGap;
    private bool _updatingSevenZipOptions;
    private bool _savingSevenZipOptions;
    private bool _sevenZipPickerPending;
    private bool _resetAllRunning;

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
        _updatingDeveloperMode = true;
        DeveloperModeSwitch.IsOn = ViewModel.CurrentPreferences.DeveloperMode;
        _updatingDeveloperMode = false;
        _updatingMsr = true;
        MsrSwitch.IsOn = ViewModel.CurrentPreferences.CreateMsrOnInitialize;
        _updatingMsr = false;
        _updatingStartup = true;
        StartupAgentSwitch.IsOn = ViewModel.CurrentAgentPreferences.StartAgentAtLogin;
        _updatingStartup = false;
        _updatingPartitionGap = true;
        PartitionGapBox.Text = MiBText(
            ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);
        _updatingPartitionGap = false;
        SyncSevenZipOptions();
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
        RefreshSevenZipOptionItems();
    }

    private void RefreshSevenZipOptionItems()
    {
        _updatingSevenZipOptions = true;
        try
        {
            SevenZipOptions.ItemsSource = new[]
            {
                ViewModel.Localization["SevenZipBundled"],
                ViewModel.Localization["SevenZipCustom"]
            };
            SevenZipOptions.SelectedIndex = string.IsNullOrWhiteSpace(
                ViewModel.CurrentAgentPreferences.SevenZipExecutablePath) ? 0 : 1;
        }
        finally
        {
            _updatingSevenZipOptions = false;
        }
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
        ContextHelp.SetDisabledReason(
            DataLocationOptions,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                ? "正在等待迁移确认；数据位置暂不可更改。"
                : "Waiting for migration confirmation; the data location cannot be changed yet.");
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
            ContextHelp.SetDisabledReason(DataLocationOptions, null);
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
        if (await DialogCoordinator.ShowAsync(warning) != ContentDialogResult.Primary)
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
        if (await DialogCoordinator.ShowAsync(confirmation) != ContentDialogResult.Primary)
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
            ViewModel.NotificationService.Publish(
                GlobalNotificationSeverity.Error,
                zh ? "重启 WinPool 失败" : "WinPool restart failed",
                zh
                    ? "数据位置已经安全提交，Agent 已按新位置恢复。请手动重启 WinPool，使主界面读取新位置。"
                    : "The data location was committed safely and the Agent resumed on it. Restart WinPool manually so the UI reads the new location.",
                "settings",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = "settings.datalocation.restart",
                    Code = "settings.datalocation.restart",
                    SystemId = SettingsSystemId,
                    Target = SettingsTarget(zh),
                    Detail = "replacement-application-start-failed"
                });
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
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Error,
            zh ? "数据位置切换失败" : "Data location switch failed",
            zh
                ? "未提交新的数据位置；请检查 Agent 与权限后重试。"
                : "The new data location was not committed. Check the Agent and permissions, then try again.",
            "settings",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "settings.datalocation.failure",
                Code = "settings.datalocation.failure",
                SystemId = SettingsSystemId,
                Target = SettingsTarget(zh),
                Detail = detail
            });
    }

    private void PublishPreferenceFailure(Exception exception)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Error,
            zh ? "设置保存失败" : "Settings save failed",
            zh
                ? "设置未保存。请检查权限或数据位置后重试。"
                : "The setting was not saved. Check permissions or the data location, then try again.",
            "settings",
            new GlobalNotificationOptions
            {
                OccurrenceKey = $"settings.preference.{exception.GetType().Name}",
                Code = "settings.preference.failure",
                SystemId = SettingsSystemId,
                Target = SettingsTarget(zh),
                Detail = $"{exception.GetType().Name}: {exception.Message}"
            });
    }

    private void PublishPathOpenFailure(string path, Exception? exception = null)
    {
        var l = ViewModel.Localization;
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Error,
            l["OpenPathFailed"],
            string.Format(l["OpenPathFailedDescription"], path),
            "settings",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "settings.open-path.failure",
                Code = "settings.open-path.failure",
                SystemId = SettingsSystemId,
                Target = SettingsTarget(zh),
                Detail = exception is null
                    ? path
                    : $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{path}"
            });
    }

    private void OpenExistingDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            PublishPathOpenFailure(directoryPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directoryPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException)
        {
            PublishPathOpenFailure(directoryPath, exception);
        }
    }

    private void OpenDataLocationButton_Click(object sender, RoutedEventArgs e) =>
        OpenExistingDirectory(StorageDataLocations.CurrentRoot);

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

    private async void DeveloperModeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingDeveloperMode)
        {
            return;
        }

        try
        {
            await ViewModel.SetDeveloperModeAsync(DeveloperModeSwitch.IsOn);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _updatingDeveloperMode = true;
            DeveloperModeSwitch.IsOn = ViewModel.CurrentPreferences.DeveloperMode;
            _updatingDeveloperMode = false;
            PublishPreferenceFailure(exception);
        }
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
        var mainWindow = (MainWindow)App.Window;
        if (await mainWindow.RequestExecutionModeAsync(requestedMode))
        {
            // The replacement process owns the UI now. Refreshing the closing
            // WinUI window can raise "The WinUI Desktop Window object has
            // already been closed" as an unhandled Xaml exception.
            return;
        }

        SyncExecutionMode();
        mainWindow.RefreshChrome();
    }

    private void PartitionGapBox_TextChanged(object sender, TextChangedEventArgs e) =>
        KeepDigitsOnly(PartitionGapBox);

    private void PartitionGapBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitPartitionGapAsync();
        }
    }

    private void PartitionGapBox_LostFocus(object sender, RoutedEventArgs e) =>
        CommitPartitionGapAsync();

    private async void CommitPartitionGapAsync()
    {
        if (!_ready || _updatingPartitionGap)
        {
            return;
        }

        if (!TryReadMib(PartitionGapBox, out var mib))
        {
            RevertPartitionGap();
            return;
        }

        try
        {
            await ViewModel.SetPartitionIgnoreSizeMibAsync(mib);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentOutOfRangeException)
        {
            RevertPartitionGap();
            PublishPreferenceFailure(exception);
        }
    }

    private void RevertPartitionGap()
    {
        _updatingPartitionGap = true;
        PartitionGapBox.Text = MiBText(
            ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);
        _updatingPartitionGap = false;
    }

    private async void SevenZipOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingSevenZipOptions || _savingSevenZipOptions || SevenZipOptions.SelectedIndex < 0)
        {
            return;
        }

        if (SevenZipOptions.SelectedIndex == 0)
        {
            await SaveSevenZipPathAsync(null);
            return;
        }

        _sevenZipPickerPending = true;
    }

    private void SevenZipOptions_DropDownOpened(object sender, object e)
    {
        if (!_ready || _updatingSevenZipOptions || _savingSevenZipOptions)
        {
            return;
        }

        // Clear an already-selected Custom item while its popup is open so a
        // second selection raises SelectionChanged. Closing without selecting
        // then restores the effective persisted mode below.
        if (SevenZipOptions.SelectedIndex == 1)
        {
            SetSevenZipOptionSelection(-1);
        }
    }

    private async void SevenZipOptions_DropDownClosed(object sender, object e)
    {
        if (!_ready
            || _updatingSevenZipOptions
            || _savingSevenZipOptions)
        {
            return;
        }

        if (_sevenZipPickerPending)
        {
            _sevenZipPickerPending = false;
            await PickCustomSevenZipAsync();
        }
        else
        {
            SyncSevenZipOptions();
        }
    }

    private async Task PickCustomSevenZipAsync()
    {
        if (_savingSevenZipOptions)
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker(SevenZipOptions.XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".exe");
            var selected = await picker.PickSingleFileAsync();
            if (selected is not null)
            {
                await SaveSevenZipPathAsync(selected.Path);
            }
            else
            {
                SyncSevenZipOptions();
            }
        }
        catch (Exception exception) when (IsSevenZipPathUiFailure(exception))
        {
            SyncSevenZipOptions();
            PublishPreferenceFailure(exception);
        }
    }

    private async Task SaveSevenZipPathAsync(string? candidate)
    {
        if (_savingSevenZipOptions)
        {
            return;
        }

        _savingSevenZipOptions = true;
        SevenZipOptions.IsEnabled = false;
        ContextHelp.SetDisabledReason(
            SevenZipOptions,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                ? "正在保存 7-Zip 路径；选择暂不可更改。"
                : "Saving the 7-Zip path; the selection cannot be changed yet.");
        try
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, DefaultSevenZipPath(), StringComparison.OrdinalIgnoreCase))
            {
                await ViewModel.SetSevenZipExecutablePathAsync(null);
                return;
            }

            // Configuration accepts only an existing absolute executable. Do
            // not inspect its capabilities or version here.
            if (!Path.IsPathFullyQualified(normalized) || !File.Exists(normalized))
            {
                await ShowMessageDialogAsync(ViewModel.Localization["SevenZipPathInvalid"]);
                return;
            }

            await ViewModel.SetSevenZipExecutablePathAsync(normalized);
        }
        catch (Exception exception) when (IsSevenZipPathUiFailure(exception))
        {
            PublishPreferenceFailure(exception);
        }
        finally
        {
            _savingSevenZipOptions = false;
            SevenZipOptions.IsEnabled = true;
            ContextHelp.SetDisabledReason(SevenZipOptions, null);
            SyncSevenZipOptions();
        }
    }

    private static bool IsSevenZipPathUiFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException;

    private void SyncSevenZipOptions()
    {
        SetSevenZipOptionSelection(
            string.IsNullOrWhiteSpace(ViewModel.CurrentAgentPreferences.SevenZipExecutablePath) ? 0 : 1);
        var effectivePath = EffectiveSevenZipPath();
        ContextHelp.Set(
            SevenZipOptions,
            $"{ViewModel.Localization["SevenZipPathHint"]}\n{effectivePath}");
        SevenZipOptions.SetValue(AutomationProperties.NameProperty, ViewModel.Localization["SevenZip"]);
        SevenZipPath.Text = effectivePath;
        ContextHelp.Set(OpenSevenZipLocationButton,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                ? "打开当前 7-Zip 可执行文件所在文件夹。"
                : "Open the folder that contains the current 7-Zip executable.");
        ContextHelp.Set(SevenZipPath, effectivePath);
    }

    private void SetSevenZipOptionSelection(int index)
    {
        _updatingSevenZipOptions = true;
        SevenZipOptions.SelectedIndex = index;
        _updatingSevenZipOptions = false;
    }

    private string EffectiveSevenZipPath() =>
        ViewModel.CurrentAgentPreferences.SevenZipExecutablePath ?? DefaultSevenZipPath();

    private void OpenSevenZipLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var executablePath = EffectiveSevenZipPath();
        if (!File.Exists(executablePath))
        {
            PublishPathOpenFailure(executablePath);
            return;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            PublishPathOpenFailure(executablePath);
            return;
        }

        OpenExistingDirectory(directory);
    }

    private static string DefaultSevenZipPath() =>
        Path.Combine(AppContext.BaseDirectory, SevenZipArchiveAdapter.BundledRelativeExecutablePath);

    /// <summary>
    /// Numeric boxes select their whole value on focus so a typed digit
    /// replaces the old number instead of appending to it.
    /// </summary>
    private void NumericBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    /// <summary>
    /// Strips everything except digits so the box always holds a pure number;
    /// the MiB unit is displayed beside the box instead of inside it.
    /// </summary>
    private static void KeepDigitsOnly(TextBox box)
    {
        var digits = new string(box.Text?.Where(char.IsDigit).ToArray() ?? []);
        if (!string.Equals(box.Text, digits, StringComparison.Ordinal))
        {
            box.Text = digits;
            box.SelectionStart = digits.Length;
        }
    }

    private static bool TryReadMib(TextBox box, out double mib)
    {
        mib = 0;
        return double.TryParse(box.Text, out mib) && mib >= 0;
    }

    private async void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _resetAllRunning)
        {
            return;
        }

        var l = ViewModel.Localization;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = l["ResetAllConfirmTitle"],
            Content = l["ResetAllConfirmMessage"],
            PrimaryButtonText = l["ResetAllConfirm"],
            CloseButtonText = l["Cancel"],
            DefaultButton = ContentDialogButton.Close
        };
        if (await DialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        _resetAllRunning = true;
        try
        {
            bool backgroundResetFailed;
            try
            {
                backgroundResetFailed = await ViewModel.ResetAllToDefaultsAsync();
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                PublishPreferenceFailure(exception);
                return;
            }

            if (App.Window is MainWindow mainWindow)
            {
                mainWindow.ApplyTheme(ViewModel.CurrentPreferences.Theme);
                mainWindow.ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
                mainWindow.RefreshChrome();
            }

            UpdateText();
            RefreshPreferenceControls();
            var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
            var resetMessage = backgroundResetFailed
                ? l["ResetAllBackgroundFailed"]
                : l["ResetAllDone"];
            ViewModel.NotificationService.Publish(
                GlobalNotificationSeverity.Info,
                l["ResetAllTitle"],
                $"{SettingsTarget(zh)}{Environment.NewLine}{resetMessage}",
                "settings",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = "settings.reset-all",
                    Code = "settings.reset-all",
                    SystemId = SettingsSystemId,
                    Target = SettingsTarget(zh),
                    Detail = resetMessage
                });
        }
        finally
        {
            _resetAllRunning = false;
        }
    }

    private async void MsrSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingMsr)
        {
            return;
        }

        try
        {
            await ViewModel.SetCreateMsrOnInitializeAsync(MsrSwitch.IsOn);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _updatingMsr = true;
            MsrSwitch.IsOn = ViewModel.CurrentPreferences.CreateMsrOnInitialize;
            _updatingMsr = false;
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

    private void CommunityButton_Click(object sender, RoutedEventArgs e)
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
            PublishExternalLinkFailure(
                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                    ? "无法打开 QQ 群链接。群号：732019606"
                    : "Could not open the QQ group link. Group: 732019606",
                exception,
                "settings.community-link.failure");
        }
    }

    private async void StartupAgentSwitch_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (!_ready || _updatingStartup)
        {
            return;
        }

        try
        {
            await ViewModel.SetStartAgentAtLoginAsync(StartupAgentSwitch.IsOn);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or InvalidOperationException)
        {
            _updatingStartup = true;
            StartupAgentSwitch.IsOn = ViewModel.CurrentAgentPreferences.StartAgentAtLogin;
            _updatingStartup = false;
            PublishPreferenceFailure(exception);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.IsRealMode))
        {
            SyncExecutionMode();
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.CurrentAgentPreferences))
        {
            _updatingStartup = true;
            StartupAgentSwitch.IsOn = ViewModel.CurrentAgentPreferences.StartAgentAtLogin;
            _updatingStartup = false;
            SyncSevenZipOptions();
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.CurrentPreferences))
        {
            RefreshPreferenceControls();
        }
    }

    private void RefreshPreferenceControls()
    {
        var wasReady = _ready;
        _ready = false;
        try
        {
            var preferences = ViewModel.CurrentPreferences;
            ThemeOptions.SelectedIndex = (int)preferences.Theme;
            AccentOptions.SelectedIndex = (int)preferences.AccentColor;
            LanguageOptions.SelectedIndex = (int)preferences.Language;
            _updatingDeveloperMode = true;
            DeveloperModeSwitch.IsOn = preferences.DeveloperMode;
            _updatingDeveloperMode = false;
            _updatingMsr = true;
            MsrSwitch.IsOn = preferences.CreateMsrOnInitialize;
            _updatingMsr = false;
            _updatingPartitionGap = true;
            PartitionGapBox.Text = MiBText(preferences.PartitionIgnoreSizeBytes);
            _updatingPartitionGap = false;
        }
        finally
        {
            _ready = wasReady;
        }
    }

    private static string MiBText(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.##");

    private void SyncExecutionMode()
    {
        _updatingMode = true;
        SettingsExecutionModeSwitch.IsEnabled = true;
        SettingsExecutionModeSwitch.IsOn = ViewModel.IsRealMode;
        ContextHelp.Set(
            SettingsExecutionModeSwitch,
            ViewModel.CanUseRealMode
                ? ViewModel.Localization["ExecutionMode"]
                : ViewModel.Localization["AdminRequired"]);
        SettingsExecutionModeSwitch.SetValue(
            AutomationProperties.NameProperty,
            ViewModel.Localization["LocalRealOperations"]);
        _updatingMode = false;
    }

    private void UpdateText()
    {
        var l = ViewModel.Localization;
        ThemeTitle.Text = l["Theme"];
        AccentTitle.Text = l["AccentColor"];
        LanguageTitle.Text = l["Language"];
        DeveloperModeTitle.Text = l["DeveloperMode"];
        DeveloperModeSwitch.SetValue(
            AutomationProperties.NameProperty,
            l["DeveloperMode"]);
        ContextHelp.Set(ThemeOptions,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "选择系统、浅色或深色主题。"
                : "Choose the system, light, or dark theme.");
        ContextHelp.Set(AccentOptions,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "选择应用强调色。"
                : "Choose the app accent color.");
        ContextHelp.Set(LanguageOptions,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "立即切换界面语言。"
                : "Switch the interface language immediately.");
        ContextHelp.Set(DeveloperModeSwitch, l["DeveloperModeDescription"]);
        ExecutionTitle.Text = l["LocalRealOperations"];
        MsrTitle.Text = l["CreateMsrOnInitialize"];
        PartitionGapTitle.Text = l["PartitionGapThreshold"];
        ContextHelp.Set(WelcomeButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "打开欢迎内容。"
                : "Open the welcome content.");
        ContextHelp.Set(StartupAgentSwitch,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "控制登录时是否启动 WinPool Agent。"
                : "Control whether the WinPool Agent starts at sign-in.");
        ContextHelp.Set(MsrSwitch,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "仅影响模拟磁盘初始化时是否创建 Microsoft 保留分区。"
                : "Only affects whether simulated disk initialization creates a Microsoft Reserved partition.");
        ContextHelp.Set(PartitionGapBox,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "以 MiB 输入隐藏小分区缝隙的阈值；按 Enter 保存。"
                : "Enter the threshold in MiB for hiding small partition gaps; press Enter to save.");
        ContextHelp.Set(DataLocationOptions,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "迁移 WinPool 数据位置；会停止 Agent 和后台监控，并在校验后重启。"
                : "Migrate the WinPool data location; it stops the Agent and background monitoring, then restarts after verification.");
        ContextHelp.Set(OpenDataLocationButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn ? "打开当前 WinPool 数据位置。" : "Open the current WinPool data location.");
        ContextHelp.Set(DataLocationPath, StorageDataLocations.CurrentRoot);
        SevenZipTitle.Text = l["SevenZip"];
        OpenSevenZipLocationButton.SetValue(AutomationProperties.NameProperty, l["OpenPath"]);
        RefreshSevenZipOptionItems();
        SyncSevenZipOptions();
        ResetAllTitle.Text = l["ResetAllTitle"];
        ResetAllButtonText.Text = l["ResetAllButton"];
        ContextHelp.Set(ResetAllButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "恢复 WinPool 设置默认值；请先阅读确认内容。"
                : "Restore WinPool settings to defaults; review the confirmation first.");
        WelcomeTitle.Text = l["Welcome"];
        WelcomeButtonText.Text = l["OpenWelcome"];
        StartupAgentTitle.Text = l["Startup"];
        DataLocationTitle.Text = l["DataLocation"];
        OpenDataLocationButton.SetValue(AutomationProperties.NameProperty, l["OpenPath"]);
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
        ContextHelp.Set(WebsiteButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn ? "在浏览器中打开官网。" : "Open the website in a browser.");
        ContextHelp.Set(UpdateButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn ? "在浏览器中查看更新。" : "View updates in a browser.");
        ContextHelp.Set(FeedbackButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn ? "在浏览器中发送反馈。" : "Send feedback in a browser.");
        ContextHelp.Set(AboutCommunityButton,
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "在浏览器中打开 WinPool QQ 交流群。"
                : "Open the WinPool QQ community in a browser.");
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
        await DialogCoordinator.ShowAsync(dialog);
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

        PublishExternalLinkFailure(
            ViewModel.Localization["OpenUpdateFailed"],
            null,
            "settings.open-uri.failure",
            uri.ToString());
    }

    private void PublishExternalLinkFailure(
        string message,
        Exception? exception,
        string code,
        string? detail = null)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        ViewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Error,
            ViewModel.Localization["Error"],
            $"{SettingsTarget(zh)}{Environment.NewLine}{message}",
            "settings",
            new GlobalNotificationOptions
            {
                OccurrenceKey = code,
                Code = code,
                SystemId = SettingsSystemId,
                Target = SettingsTarget(zh),
                Detail = exception is null
                    ? detail ?? string.Empty
                    : $"{exception.GetType().Name}: {exception.Message}"
            });
    }

    private string SettingsTarget(bool zh) => zh ? "本机 WinPool 设置" : "Local WinPool settings";

    private string SettingsSystemId => ViewModel.SystemCatalog.Systems
        .First(system => system.IsLocal)
        .Id;

}
