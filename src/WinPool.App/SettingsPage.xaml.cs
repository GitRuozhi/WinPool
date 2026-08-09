using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Core;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Ipc;
using WinPool.ToolManagement;
using DomainStorageLocationMode = WinPool.Domain.StorageLocationMode;

namespace WinPool_App;

public sealed partial class SettingsPage : Page
{
    private bool _ready;
    private bool _updatingMode;
    private bool _updatingDataLocation;
    private readonly ToolCatalog _toolCatalog = new();
    private readonly AgentStartupRegistration _agentStartup = new();
    private readonly Dictionary<ToolId, TextBlock> _toolStatuses = [];
    private readonly JsonToolPathConfiguration _toolPaths = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPool",
            "tool-paths.json"));

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
        StartupAgentCheckBox.IsChecked = _agentStartup.IsEnabled();
        _updatingDataLocation = true;
        DataLocationOptions.SelectedIndex = (int)StorageDataLocations.Mode;
        _updatingDataLocation = false;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        BuildExternalToolRows();
        UpdateText();
        SyncExecutionMode();
        _ready = true;
        _ = RefreshExternalToolsAsync();
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
                ? "切换会停止托盘 Agent、后台监控和正在运行的测试。源数据会保留；复制与 SQLite 逻辑校验成功后才提交位置指针，随后 WinPool 会重启。"
                : "Switching stops the tray Agent, background monitoring, and any running test. Source data is retained; the location pointer is committed only after copy and SQLite logical verification, then WinPool restarts.",
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
                UserConfirmedActiveTestCancellation: true,
                CorrelationId.New()),
            shutdownTimeout.Token);
        if (shutdown.Value is ShutdownResponse response && !response.Result.Completed)
        {
            PublishDataLocationFailure(zh, "agent-shutdown-incomplete");
            return;
        }

        if (!await WaitForAgentExitAsync(shutdownTimeout.Token))
        {
            PublishDataLocationFailure(zh, "agent-exit-timeout");
            return;
        }

        using var agentExclusion = TryAcquireAgentMigrationExclusion();
        if (agentExclusion is null)
        {
            PublishDataLocationFailure(zh, "agent-restarted-during-handoff");
            return;
        }

        using var migrationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var manager = new StorageLocationManager(
            StorageDataLocations.StandardRoot,
            StorageDataLocations.PortableRoot,
            new StoppedAgentWriteCoordinator());
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
        if (!StartReplacementApplication())
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

    private static async Task<bool> WaitForAgentExitAsync(CancellationToken cancellationToken)
    {
        while (File.Exists(NamedPipeAgentConnection.DefaultEndpointPath))
        {
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
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

    private static bool StartReplacementApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(ApplicationStartupOptions.StorageLocationHandoffArgument);
        startInfo.ArgumentList.Add(ApplicationStartupOptions.WaitForProcessArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private static AgentMigrationExclusion? TryAcquireAgentMigrationExclusion()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        var mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\WinPool.Agent.{IpcIdentity.HashUserSid(sid)[..24]}",
            out var ownsMutex);
        if (!ownsMutex)
        {
            mutex.Dispose();
            return null;
        }

        return new AgentMigrationExclusion(mutex);
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

    private sealed class StoppedAgentWriteCoordinator : IStorageWriteQuiescenceCoordinator
    {
        public Task<IAsyncDisposable> QuiesceAndFlushAsync(
            CorrelationId correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AgentMigrationExclusion(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Release()
        {
            var owned = Interlocked.Exchange(ref _mutex, null);
            if (owned is null)
            {
                return;
            }

            owned.ReleaseMutex();
            owned.Dispose();
        }

        public void Dispose() => Release();
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
        BuildExternalToolRows();
        _ = RefreshExternalToolsAsync();
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

    private void WelcomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow mainWindow)
        {
            mainWindow.ShowWelcome();
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
            _agentStartup.SetEnabled(StartupAgentCheckBox.IsChecked == true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            StartupAgentCheckBox.IsChecked = _agentStartup.IsEnabled();
            await ShowToolDialogAsync(exception.Message);
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
            ? "登录启动"
            : "Windows sign-in";
        StartupAgentCheckBox.Content =
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "登录 Windows 时启动托盘 Agent（默认关闭）"
                : "Start the tray Agent at Windows sign-in (off by default)";
        DataLocationTitle.Text = l["DataLocation"];
        DataLocationPath.Text = StorageDataLocations.CurrentRoot;
        _updatingDataLocation = true;
        DataLocationOptions.ItemsSource = new[] { l["StandardLocation"], l["PortableLocation"] };
        DataLocationOptions.SelectedIndex = (int)StorageDataLocations.Mode;
        _updatingDataLocation = false;
        ExternalToolsTitle.Text = l["ExternalTools"];
        ExternalToolsDescription.Text =
            l.EffectiveLanguage == LanguagePreference.ZhCn
                ? "DiskSpd、fio、Dite FileGen 与 RAMMap 不随 WinPool 发布；RoboCopy 由 Windows 提供。可检测状态、设置自定义路径；已登记的便携压缩包可在下载、哈希、签名和二次确认后安装，其他工具打开官方来源。"
                : "DiskSpd, fio, Dite FileGen, and RAMMap are not bundled with WinPool; RoboCopy is provided by Windows. Detect or configure them; registered portable archives can be installed after download, hashing, signature verification, and a second confirmation, while other tools open their official source.";
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

    private void BuildExternalToolRows()
    {
        ExternalToolRows.Children.Clear();
        _toolStatuses.Clear();
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        foreach (var descriptor in _toolCatalog.List())
        {
            var status = new TextBlock
            {
                Text = zh ? "正在检测…" : "Detecting…",
                TextWrapping = TextWrapping.Wrap
            };
            _toolStatuses[descriptor.Id] = status;

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            actions.Children.Add(ActionButton(
                zh ? "检测" : "Detect",
                descriptor.Id,
                DetectTool_Click));
            actions.Children.Add(ActionButton(
                zh ? "自定义路径" : "Custom path",
                descriptor.Id,
                SelectToolPath_Click));
            actions.Children.Add(ActionButton(
                zh ? "清除路径" : "Clear path",
                descriptor.Id,
                ClearToolPath_Click));
            actions.Children.Add(ActionButton(
                zh ? "安装 / 获取" : "Install / Get",
                descriptor.Id,
                InstallTool_Click));

            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(
                new TextBlock
                {
                    Text = descriptor.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            content.Children.Add(
                new TextBlock
                {
                    Text = descriptor.Purpose,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                });
            content.Children.Add(status);
            content.Children.Add(actions);
            ExternalToolRows.Children.Add(content);
        }
    }

    private static Button ActionButton(
        string text,
        ToolId toolId,
        RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(9, 4, 9, 4),
            Tag = toolId.Value
        };
        button.Click += handler;
        return button;
    }

    private async Task RefreshExternalToolsAsync()
    {
        foreach (var descriptor in _toolCatalog.List())
        {
            await DetectToolAsync(descriptor.Id);
        }
    }

    private async void DetectTool_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetToolId(sender, out var toolId))
        {
            await DetectToolAsync(toolId);
        }
    }

    private async Task DetectToolAsync(ToolId toolId)
    {
        if (!_toolStatuses.TryGetValue(toolId, out var status))
        {
            return;
        }

        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        status.Text = zh ? "正在检测…" : "Detecting…";
        ApplicationResult<ToolState> result;
        if (ViewModel.AgentConnection is not null)
        {
            var response = await ViewModel.AgentConnection.SendAsync(
                new DetectAgentToolRequest(toolId, CorrelationId.New()),
                CancellationToken.None);
            result = response.Value is ToolStateResponse value
                ? new ApplicationResult<ToolState>(
                    response.Status,
                    value.ToolState,
                    response.Messages,
                    response.CorrelationId)
                : new ApplicationResult<ToolState>(
                    response.Status,
                    null,
                    response.Messages,
                    response.CorrelationId);
        }
        else
        {
            var registry = new ExternalToolRegistry(
                _toolCatalog,
                new ToolPathDiscovery(_toolPaths, new EnvironmentToolSearchPath()),
                new WindowsToolVersionProbe(),
                new Sha256ToolFileHasher());
            result = await registry.DetectAsync(toolId, CancellationToken.None);
        }

        status.Text = FormatToolState(result.Value, result.Status, zh);
    }

    private async void SelectToolPath_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetToolId(sender, out var toolId)
            || !_toolCatalog.TryGet(toolId, out var descriptor))
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (!descriptor.ExecutableFileNames.Contains(
                Path.GetFileName(file.Path),
                StringComparer.OrdinalIgnoreCase))
        {
            await ShowToolDialogAsync(
                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                    ? "文件名不符合该工具的已登记可执行文件名。"
                    : "The file name does not match a registered executable for this tool.");
            return;
        }

        await _toolPaths.SetCustomExecutablePathAsync(
            toolId,
            file.Path,
            CancellationToken.None);
        await DetectToolAsync(toolId);
    }

    private async void ClearToolPath_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetToolId(sender, out var toolId))
        {
            return;
        }

        await _toolPaths.SetCustomExecutablePathAsync(
            toolId,
            null,
            CancellationToken.None);
        await DetectToolAsync(toolId);
    }

    private async void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetToolId(sender, out var toolId)
            || !_toolCatalog.TryGet(toolId, out var descriptor))
        {
            return;
        }

        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        if (descriptor.InstallerKind is null)
        {
            if (toolId == KnownToolIds.RoboCopy)
            {
                await ShowToolDialogAsync(
                    zh
                        ? $"{descriptor.DisplayName} 是 Windows 组件，WinPool 不会单独安装它。"
                        : $"{descriptor.DisplayName} is a Windows component and is not installed separately by WinPool.");
                return;
            }

            var sourceDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
                Title = zh
                    ? $"获取 {descriptor.DisplayName}"
                    : $"Get {descriptor.DisplayName}",
                Content = zh
                    ? "该过渡工具不随 WinPool 发布。将打开登记的官方来源；下载或安装后，请返回并选择 Dite.exe 的自定义路径。"
                    : "This transitional tool is not bundled with WinPool. Its registered official source will open; after download or installation, return and select the custom Dite.exe path.",
                PrimaryButtonText = zh ? "打开官方来源" : "Open official source",
                CloseButtonText = zh ? "取消" : "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await sourceDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await OpenAsync(descriptor.OfficialInstallSource);
            }
            return;
        }

        var planned = await new PlanningOnlyToolInstaller(_toolCatalog).PlanAsync(
            toolId,
            ToolInstallLocation.PerUserManagedDirectory,
            CancellationToken.None);
        if (planned.Value is null)
        {
            await ShowToolDialogAsync(
                zh ? "无法生成官方安装计划。" : "The official install plan could not be created.");
            return;
        }

        if (descriptor.InstallerKind == ToolInstallerKind.PortableArchive)
        {
            await InstallPortableToolAsync(descriptor, planned.Value, zh);
            return;
        }

        if (descriptor.InstallerKind == ToolInstallerKind.Msi)
        {
            await InstallMsiToolAsync(descriptor, planned.Value, zh);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh ? $"获取 {descriptor.DisplayName}" : $"Get {descriptor.DisplayName}",
            Content = zh
                ? "该工具不随 WinPool 发布。当前阶段将打开登记的官方安装源；不会静默下载或执行安装程序。安装后请返回并重新检测。"
                : "This tool is not bundled with WinPool. The registered official source will open; WinPool will not silently download or run an installer. Return and detect again after installation.",
            PrimaryButtonText = zh ? "打开官方来源" : "Open official source",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await OpenAsync(planned.Value.OfficialSource);
        }
    }

    private async Task InstallPortableToolAsync(
        ToolDescriptor descriptor,
        ToolInstallPlan initialPlan,
        bool zh)
    {
        var downloadDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh
                ? $"下载并检查 {descriptor.DisplayName}"
                : $"Download and inspect {descriptor.DisplayName}",
            Content = zh
                ? $"WinPool 将仅从已登记的官方 HTTPS 来源下载压缩包：\n{descriptor.OfficialInstallSource}\n\n下载后会计算 SHA-256、检查压缩包结构并在安装前再次显示确认。工具不会随 WinPool 发布。"
                : $"WinPool will download the archive only from the registered official HTTPS source:\n{descriptor.OfficialInstallSource}\n\nIt will calculate SHA-256, inspect the archive, and ask again before installation. The tool is not bundled with WinPool.",
            PrimaryButtonText = zh ? "下载并检查" : "Download and inspect",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await downloadDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WinPool/0.21");
        var dataRoot = StorageDataLocations.CurrentRoot;
        var installer = new ControlledPortableToolInstaller(
            _toolCatalog,
            new HttpToolPackageDownloader(httpClient),
            new WindowsToolExecutableTrustVerifier(),
            _toolPaths,
            Path.Combine(dataRoot, "tool-downloads"),
            Path.Combine(dataRoot, "tools"));
        var prepared = await installer.PrepareAsync(
            initialPlan,
            CancellationToken.None);
        if (!prepared.IsSuccess || prepared.Value is null)
        {
            await ShowToolDialogAsync(
                prepared.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "下载或检查官方压缩包失败。" : "The official archive could not be downloaded or inspected."));
            return;
        }

        var review = prepared.Value;
        var confirmDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh
                ? $"确认安装 {descriptor.DisplayName}"
                : $"Confirm {descriptor.DisplayName} installation",
            Content = zh
                ? $"来源：{review.FinalizedPlan.OfficialSource}\nSHA-256：{review.PackageSha256}\n压缩包条目：{review.SelectedArchiveEntry}\n目标：WinPool 当前用户工具目录\n\n安装时会再次校验包哈希，并拒绝没有受信任 Authenticode 签名的可执行文件。"
                : $"Source: {review.FinalizedPlan.OfficialSource}\nSHA-256: {review.PackageSha256}\nArchive entry: {review.SelectedArchiveEntry}\nTarget: WinPool per-user tools directory\n\nInstallation will verify the package hash again and reject an executable without a trusted Authenticode signature.",
            PrimaryButtonText = zh ? "确认安装" : "Install",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        var confirmed = await confirmDialog.ShowAsync() == ContentDialogResult.Primary;
        var authorization = ToolInstallAuthorization.Authorize(
            review.FinalizedPlan,
            confirmed,
            DateTimeOffset.UtcNow,
            CorrelationId.New());
        if (!authorization.IsSuccess || authorization.Value is null)
        {
            return;
        }

        var installed = await installer.InstallAsync(
            authorization.Value,
            CancellationToken.None);
        if (!installed.IsSuccess || installed.Value is null)
        {
            await ShowToolDialogAsync(
                installed.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "安装失败。" : "Installation failed."));
            return;
        }

        await ShowToolDialogAsync(
            zh
                ? $"{descriptor.DisplayName} 已安装到 WinPool 当前用户工具目录，并已配置自定义路径。"
                : $"{descriptor.DisplayName} was installed into the WinPool per-user tools directory and its custom path was configured.");
        await DetectToolAsync(descriptor.Id);
    }

    private async Task InstallMsiToolAsync(
        ToolDescriptor descriptor,
        ToolInstallPlan initialPlan,
        bool zh)
    {
        if (ViewModel.AgentConnection is null)
        {
            await ShowToolDialogAsync(
                zh ? "WinPool Agent 不可用，无法执行受控提权安装。" :
                "WinPool Agent is unavailable, so the controlled elevated installation cannot run.");
            return;
        }

        var downloadDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh ? $"下载并检查 {descriptor.DisplayName}" : $"Download and inspect {descriptor.DisplayName}",
            Content = zh
                ? $"WinPool 将从固定的 fio 官方 GitHub 发布资产下载 MSI：\n{descriptor.OfficialInstallSource}\n\n包必须匹配目录中固定的 SHA-256，之后还会再次确认并显示 UAC。fio 不随 WinPool 发布。"
                : $"WinPool will download the MSI from the pinned official fio GitHub release asset:\n{descriptor.OfficialInstallSource}\n\nThe package must match the catalog-pinned SHA-256. A second confirmation and UAC prompt follow. fio is not bundled with WinPool.",
            PrimaryButtonText = zh ? "下载并校验" : "Download and verify",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await downloadDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (_toolStatuses.TryGetValue(descriptor.Id, out var status))
        {
            status.Text = zh ? "正在下载并校验 MSI…" : "Downloading and verifying MSI…";
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WinPool/0.21");
        var installer = new ControlledMsiToolInstaller(
            _toolCatalog,
            new HttpToolPackageDownloader(httpClient),
            StorageDataLocations.CurrentRoot);
        var prepared = await installer.PrepareAsync(initialPlan, CancellationToken.None);
        if (!prepared.IsSuccess || prepared.Value is null)
        {
            await ShowToolDialogAsync(
                prepared.Messages.FirstOrDefault()?.DiagnosticText ??
                (zh ? "MSI 下载或哈希校验失败。" : "The MSI download or hash verification failed."));
            await DetectToolAsync(descriptor.Id);
            return;
        }

        var review = prepared.Value;
        var confirmDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh ? $"确认安装 {descriptor.DisplayName}" : $"Confirm {descriptor.DisplayName} installation",
            Content = zh
                ? $"来源：{review.FinalizedPlan.OfficialSource}\nSHA-256：{review.PackageSha256}\n\n将启动一次性提权 Broker，并显示 Windows UAC。Broker 会重新校验暂存路径和哈希，再以可见进度、禁止自动重启的方式调用 Windows Installer。"
                : $"Source: {review.FinalizedPlan.OfficialSource}\nSHA-256: {review.PackageSha256}\n\nA one-shot elevated Broker and Windows UAC prompt will start. The Broker rechecks the staging path and hash before invoking Windows Installer with visible progress and automatic restart disabled.",
            PrimaryButtonText = zh ? "确认并请求 UAC" : "Confirm and request UAC",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            await DetectToolAsync(descriptor.Id);
            return;
        }

        if (_toolStatuses.TryGetValue(descriptor.Id, out status))
        {
            status.Text = zh ? "等待 UAC / Windows Installer…" : "Waiting for UAC / Windows Installer…";
        }

        var result = await ViewModel.AgentConnection.SendAsync(
            new InstallAgentMsiToolRequest(
                review.FinalizedPlan,
                review.PackageRelativePath,
                UserConfirmed: true,
                CorrelationId.New()),
            CancellationToken.None);
        if (!result.IsSuccess || result.Value is not MsiToolInstallResponse response)
        {
            await ShowToolDialogAsync(
                result.Messages.FirstOrDefault()?.DiagnosticText ??
                (zh ? "fio MSI 安装失败或被取消。" : "The fio MSI installation failed or was cancelled."));
            await DetectToolAsync(descriptor.Id);
            return;
        }

        await ShowToolDialogAsync(
            response.Result.MsiInstallEvidence?.RebootRequired == true
                ? (zh ? "fio 已安装。Windows Installer 报告需要重启；WinPool 不会自动重启系统。" :
                    "fio was installed. Windows Installer reports that a restart is required; WinPool will not restart automatically.")
                : (zh ? "fio 已安装，WinPool 将重新检测工具路径。" :
                    "fio was installed. WinPool will detect its executable path again."));
        var installedFioPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "fio",
            "fio.exe");
        if (descriptor.Id == KnownToolIds.Fio && File.Exists(installedFioPath))
        {
            await _toolPaths.SetCustomExecutablePathAsync(
                descriptor.Id,
                installedFioPath,
                CancellationToken.None);
        }
        await DetectToolAsync(descriptor.Id);
    }

    private async Task ShowToolDialogAsync(string content)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).RequestedTheme,
            Title = zh ? "外部工具" : "External tools",
            Content = content,
            CloseButtonText = zh ? "关闭" : "Close"
        };
        await dialog.ShowAsync();
    }

    private static bool TryGetToolId(object sender, out ToolId toolId)
    {
        if (sender is Button { Tag: string value }
            && !string.IsNullOrWhiteSpace(value))
        {
            toolId = new ToolId(value);
            return true;
        }

        toolId = default;
        return false;
    }

    private static string FormatToolState(
        ToolState? state,
        ApplicationStatus applicationStatus,
        bool zh)
    {
        if (state is null)
        {
            return zh
                ? $"检测失败：{applicationStatus}"
                : $"Detection failed: {applicationStatus}";
        }

        var availability = state.Availability switch
        {
            ToolAvailability.Available => zh ? "可用" : "Available",
            ToolAvailability.NotFound => zh ? "未安装或未发现" : "Not installed or not found",
            ToolAvailability.UnsupportedVersion => zh ? "版本不受支持" : "Unsupported version",
            ToolAvailability.IdentityChanged => zh ? "文件身份已变化" : "File identity changed",
            ToolAvailability.InvalidSignature => zh ? "签名无效" : "Invalid signature",
            _ => zh ? "配置有误" : "Misconfigured"
        };
        var details = new[] { state.Version, state.ExecutablePath }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join(" · ", new[] { availability }.Concat(details));
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
