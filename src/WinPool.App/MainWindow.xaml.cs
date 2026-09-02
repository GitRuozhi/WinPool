using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using IAgentConnection = WinPool.Application.IAgentConnection;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinPool_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _initialized;
    private bool _updatingMode;
    private bool _updatingNavigation;
    private bool _requestingElevation;
    private bool _realWarningDismissed;
    private readonly ApplicationStartupTarget _startupTarget;
    private string _preferredShellPage = "Manage";
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly IElevationRestartService _elevationRestartService;
    private readonly IWorkspaceStateService _workspaceStateService;
    private readonly DispatcherTimer _notificationDismissTimer;
    private InputNonClientPointerSource? _nonClientPointerSource;
    private WelcomeWindow? _welcomeWindow;

    public WorkspaceViewModel ViewModel { get; }

    public IGlobalNotificationService NotificationService { get; }

    public ObservableCollection<ShellNavigationItem> ShellNavigationItems { get; } = [];

    public ShellNavigationItem? SelectedShellItem { get; set; }

    public MainWindow(
        ApplicationStartupOptions startupOptions,
        IAgentConnection? agentConnection = null)
    {
        NotificationService = new GlobalNotificationService();
        _elevationRestartService = new WindowsElevationRestartService();
        _workspaceStateService = agentConnection is null
            ? new EphemeralWorkspaceStateService()
            : new AgentBackedWorkspaceStateService(agentConnection);
        _startupTarget = startupOptions.Target;
        var importExportService = new DesktopExportService();
        ViewModel = new WorkspaceViewModel(
            agentConnection is null
                ? new WindowsHardwareInventoryProvider()
                : new AgentBackedHardwareInventoryProvider(agentConnection),
            new WindowsPrivilegeService(),
            new LocalUserPreferencesService(),
            importExportService,
            agentConnection is null
                ? new LocalStorageSystemRepository()
                : new AgentBackedStorageSystemRepository(agentConnection),
            new SimulationOperationService(),
            NotificationService,
            agentConnection is null
                ? new EphemeralMachineRecordService()
                : new AgentBackedMachineRecordService(agentConnection),
            new GlobalCommandLogService(),
            _workspaceStateService,
            agentConnection);
        if (startupOptions.EnterRealModeAfterElevation)
        {
            ViewModel.TrySetExecutionMode(ExecutionMode.Real);
        }

        InitializeComponent();

        ((System.Collections.Specialized.INotifyCollectionChanged)NotificationService.Notifications)
            .CollectionChanged += Notifications_CollectionChanged;
        _notificationDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _notificationDismissTimer.Tick += NotificationDismissTimer_Tick;
        _notificationDismissTimer.Start();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/CAppIcon.ico");
        var windowScale = AppWindowPlacement.GetWindowScale(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var minimumSize = AppWindowPlacement.ScaleLogicalSize(
                new SizeInt32(480, 300),
                windowScale);
            presenter.PreferredMinimumWidth = minimumSize.Width;
            presenter.PreferredMinimumHeight = minimumSize.Height;
        }
        AppWindow.Resize(AppWindowPlacement.ScaleLogicalSize(
            new SizeInt32(1440, 900),
            windowScale));
        AppWindowPlacement.CenterOnWorkArea(AppWindow);
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        Closed += MainWindow_Closed;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        BuildShellNavigation();
        RegisterShellKeyboardAccelerators();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await ViewModel.InitializePreferencesAsync();
            _preferredShellPage = ViewModel.CurrentPreferences.LastActivePage;
        }
        catch (Exception exception)
        {
            ViewModel.NotificationService.PublishError(
                "WinPool",
                $"偏好设置初始化失败：{exception.Message}",
                "startup",
                "startup-preferences-failed");
        }
        ApplyTheme(ViewModel.CurrentPreferences.Theme);
        ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
        NavigateStartupPage();
        ViewModel.BeginWorkspacePrepare();
        try
        {
            // Show the shell first. Agent readiness is required for SQLite
            // restore and inventory, not for painting tab structure.
            await App.InitialAgentConnectionTask;
            ViewModel.NotifyWorkspaceLoading();
            await ViewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            if (!App.InitialAgentWarningPublished)
            {
                ViewModel.NotificationService.PublishError(
                    "WinPool",
                    $"工作区初始化失败：{exception.Message}",
                    "startup",
                    "startup-initialize-failed");
            }
        }
        finally
        {
            ViewModel.CompleteWorkspacePrepare();
            // The Edit page captures the active snapshot once at navigation.
            // When the workspace finished loading after a startup navigation,
            // re-create it so the page does not stay on the pre-init snapshot.
            if (SelectedShellItem?.Page == ShellPageKind.Create)
            {
                SelectShellPage(ShellPageKind.Create);
            }
        }
        ApplyTheme(ViewModel.CurrentPreferences.Theme);
        ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
        RefreshChrome();
        UpdateCaptionInset();
        UpdateCaptionButtonColors();
    }

    private void NavigateStartupPage()
    {
        if (_startupTarget is not (ApplicationStartupTarget.None or ApplicationStartupTarget.Welcome))
        {
            ActivateTarget(_startupTarget);
            return;
        }

        if (Enum.TryParse<ShellPageKind>(_preferredShellPage, out var preferredPage)
            && preferredPage != ShellPageKind.Manage)
        {
            SelectShellPage(preferredPage);
            return;
        }

        SelectShellPage(ShellPageKind.Manage);
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        App.StopActivationChannel();
        try
        {
            if (ViewModel.Monitoring.UsesAgent)
            {
                await ViewModel.Monitoring.DetachAsync();
            }
            else
            {
                await ViewModel.Monitoring.StopAsync();
            }
            ViewModel.Monitoring.Dispose();
            await _workspaceStateService.SaveAsync(
                ViewModel.CaptureUiState((SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString()));
            await ViewModel.SetLastActivePageAsync(
                (SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ShowWelcomeWindow()
    {
        if (_welcomeWindow is not null)
        {
            _welcomeWindow.Activate();
            return;
        }

        _welcomeWindow = new WelcomeWindow(ViewModel.Localization);
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Activate();
    }

    internal void ShowStartupWelcome()
    {
        if (_startupTarget is ApplicationStartupTarget.None
            or ApplicationStartupTarget.Welcome)
        {
            ShowWelcomeWindow();
        }
    }

    internal void ShowWelcome() => RootGrid.DispatcherQueue.TryEnqueue(ShowWelcomeWindow);

    internal void ActivateTarget(ApplicationStartupTarget target)
    {
        if (target == ApplicationStartupTarget.Welcome)
        {
            ShowWelcome();
            return;
        }

        SelectShellPage(target switch
        {
            ApplicationStartupTarget.Edit => ShellPageKind.Create,
            ApplicationStartupTarget.Test => ShellPageKind.Test,
            ApplicationStartupTarget.Monitor => ShellPageKind.Monitor,
            ApplicationStartupTarget.Development => ShellPageKind.Development,
            ApplicationStartupTarget.Settings => ShellPageKind.Settings,
            _ => ShellPageKind.Manage
        });
    }

    public void ShowWorkspace()
    {
        SelectShellPage(ShellPageKind.Manage);
    }

    public void ShowSettings()
    {
        SelectShellPage(ShellPageKind.Settings);
    }

    public void ShowCreate() => ShowEdit(null);

    public void ShowEdit(string? targetStableId)
    {
        SelectShellPage(ShellPageKind.Create, targetStableId);
    }

    public void ApplyTheme(ThemePreference preference)
    {
        RootGrid.RequestedTheme = preference switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RootGrid.DispatcherQueue.TryEnqueue(UpdateCaptionButtonColors);
    }

    public void RefreshChrome()
    {
        var language = ViewModel.Localization.EffectiveLanguage;
        var suffix = ViewModel.PrivilegeState == PrivilegeState.Administrator
            ? (language == LanguagePreference.ZhCn ? " [管理员]" : " [Administrator]")
            : string.Empty;
        WindowTitleText.Text = $"WinPool{suffix}";
        Title = WindowTitleText.Text;
        AppWindow.Title = WindowTitleText.Text;
        LocalRealOperationsLabel.Text = ViewModel.Localization["LocalRealOperations"];
        LocalRealOperationsSwitch.SetValue(
            AutomationProperties.NameProperty,
            ViewModel.Localization["LocalRealOperations"]);
        ToolTipService.SetToolTip(
            LocalRealOperationsSwitch,
            ViewModel.CanUseRealMode ? ViewModel.Localization["ExecutionMode"] : ViewModel.Localization["AdminRequired"]);
        LocalRealOperationsSwitch.IsEnabled = true;
        LocalRealOperationsWarning.Title = ViewModel.Localization["PreviewWarningTitle"];
        LocalRealOperationsWarning.Message = ViewModel.Localization["PreviewWarningMessage"];
        RefreshShellNavigationText();
        UpdateShellNavigationTextVisibility();
        UpdateActiveSystemName();
        SyncModeSwitch();
    }

    private void UpdateActiveSystemName()
    {
        // The field is always visible in the title bar, independent of the
        // currently selected shell page.
        var system = ViewModel.SelectedSystem;
        var prefix = system is null
            ? string.Empty
            : system.IsLocal
                ? (ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[本机]" : "[Local]")
                : (ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]");
        ActiveSystemBadge.Visibility = system is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ActiveSystemNameText.Text = system is null ? string.Empty : $"{prefix} {system.DisplayName}";
    }

    private void UpdateCaptionInset()
    {
        var right = Math.Max(8, AppWindow.TitleBar.RightInset + 8);
        ModeControls.Margin = new Thickness(8, 0, right, 0);
    }

    private async void LocalRealOperationsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingMode)
        {
            return;
        }

        await RequestExecutionModeAsync(
            LocalRealOperationsSwitch.IsOn
                ? ExecutionMode.Real
                : ExecutionMode.Simulation);
    }

    public async Task RequestExecutionModeAsync(ExecutionMode requestedMode)
    {
        if (requestedMode == ExecutionMode.Simulation)
        {
            ViewModel.TrySetExecutionMode(ExecutionMode.Simulation);
            _realWarningDismissed = false;
            LocalRealOperationsWarning.IsOpen = false;
            SyncModeSwitch();
            return;
        }

        SyncModeSwitch();
        if (_requestingElevation || RootGrid.XamlRoot is null)
        {
            return;
        }

        _requestingElevation = true;
        try
        {
            var localization = ViewModel.Localization;
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme,
                Title = localization["PreviewWarningTitle"],
                Content = localization["PreviewConfirmation"],
                PrimaryButtonText = ViewModel.CanUseRealMode
                    ? localization["Confirm"]
                    : localization["RestartAsAdministrator"],
                CloseButtonText = localization["Cancel"],
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            if (ViewModel.CanUseRealMode)
            {
                ViewModel.TrySetExecutionMode(ExecutionMode.Real);
                _realWarningDismissed = false;
                LocalRealOperationsWarning.IsOpen = true;
                return;
            }

            var restart = await _elevationRestartService.RestartElevatedAsync(
                ApplicationStartupOptions.ElevatedRealArgument);
            if (restart.Status == ElevationRestartStatus.Started)
            {
                Close();
                return;
            }

            if (restart.Status == ElevationRestartStatus.Failed)
            {
                NotificationService.PublishError(
                    localization["Error"],
                    $"{localization["ElevationFailed"]} {restart.ErrorMessage}".Trim(),
                    "elevation",
                    $"elevation:{DateTimeOffset.UtcNow.Ticks}");
            }
        }
        finally
        {
            _requestingElevation = false;
            SyncModeSwitch();
        }
    }

    private void SyncModeSwitch()
    {
        _updatingMode = true;
        LocalRealOperationsSwitch.IsOn = ViewModel.IsRealMode;
        if (ViewModel.IsRealMode
            && !_realWarningDismissed
            && !LocalRealOperationsWarning.IsOpen)
        {
            LocalRealOperationsWarning.IsOpen = true;
        }
        _updatingMode = false;
    }

    public void ApplyAccentColor(AccentColorPreference preference)
    {
        var useSystemAccent = _accessibilitySettings.HighContrast
            || preference == AccentColorPreference.System;
        var color = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.Accent)
            : preference switch
        {
            AccentColorPreference.Blue => Color.FromArgb(255, 0x00, 0x78, 0xD4),
            AccentColorPreference.Cyan => Color.FromArgb(255, 0x00, 0x99, 0xBC),
            AccentColorPreference.Green => Color.FromArgb(255, 0x10, 0x7C, 0x10),
            AccentColorPreference.Purple => Color.FromArgb(255, 0x74, 0x4D, 0xA9),
            AccentColorPreference.Orange => Color.FromArgb(255, 0xCA, 0x50, 0x10),
            AccentColorPreference.Red => Color.FromArgb(255, 0xD1, 0x34, 0x38),
            _ => _uiSettings.GetColorValue(UIColorType.Accent)
        };

        var light1 = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.AccentLight1)
            : Blend(color, 0.18);
        var light2 = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.AccentLight2)
            : Blend(color, 0.35);
        var light3 = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.AccentLight3)
            : Blend(color, 0.52);
        var dark1 = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.AccentDark1)
            : Blend(color, -0.16);
        var dark2 = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.AccentDark2)
            : Blend(color, -0.30);
        var dark3 = useSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.AccentDark3)
            : Blend(color, -0.44);
        var foreground = ContrastingText(color);
        var hover = Color.FromArgb(0x36, color.R, color.G, color.B);
        var pressed = Color.FromArgb(0x58, color.R, color.G, color.B);
        var currentIsLight = RootGrid.ActualTheme == ElementTheme.Light;
        var fill = currentIsLight ? dark1 : light2;
        var text = currentIsLight ? dark2 : light3;
        var fillSecondary = WithAlpha(fill, 0xE5);
        var fillTertiary = WithAlpha(fill, 0xCC);
        var textSecondary = WithAlpha(text, 0xE5);
        var textTertiary = WithAlpha(text, 0xCC);
        var textOnFill = ContrastingText(fill);

        SetOwnedColor("WinPoolAccentColor", color);
        SetOwnedColor("WinPoolAccentHoverColor", hover);
        SetOwnedColor("WinPoolAccentPressedColor", pressed);
        SetOwnedColor("WinPoolAccentBorderColor", fill);
        SetOwnedColor("WinPoolAccentForegroundColor", foreground);
        SetOwnedBrushColor("WinPoolAccentBrush", color);
        SetOwnedBrushColor("WinPoolAccentHoverBrush", hover);
        SetOwnedBrushColor("WinPoolAccentPressedBrush", pressed);
        SetOwnedBrushColor("WinPoolAccentBorderBrush", fill);
        SetOwnedBrushColor("WinPoolAccentForegroundBrush", foreground);
        SetOwnedBrushColor("AccentFillColorDefaultBrush", fill);
        SetOwnedBrushColor("AccentFillColorSecondaryBrush", fillSecondary);
        SetOwnedBrushColor("AccentFillColorTertiaryBrush", fillTertiary);
        SetOwnedBrushColor("AccentFillColorSelectedTextBackgroundBrush", color);
        SetOwnedBrushColor("AccentTextFillColorPrimaryBrush", text);
        SetOwnedBrushColor("AccentTextFillColorSecondaryBrush", textSecondary);
        SetOwnedBrushColor("AccentTextFillColorTertiaryBrush", textTertiary);
        SetOwnedBrushColor("TextOnAccentFillColorPrimaryBrush", textOnFill);
        SetOwnedBrushColor("TextOnAccentFillColorDefaultBrush", textOnFill);
        SetOwnedBrushColor("FocusStrokeColorOuterBrush", fill);
        SetOwnedBrushColor("ListViewItemSelectionIndicatorBrush", fill);
        SetOwnedBrushColor("ToggleSwitchFillOn", fill);
        SetOwnedBrushColor("ToggleSwitchFillOnPointerOver", fillSecondary);
        SetOwnedBrushColor("ToggleSwitchFillOnPressed", fillTertiary);
        SetOwnedBrushColor("ToggleSwitchStrokeOn", fill);
        UpdateShellNavigationAccent();
    }

    private void BuildShellNavigation()
    {
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Manage, string.Empty, "\uE80F"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Create, string.Empty, "\uE710"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Test, string.Empty, "\uE768"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Monitor, string.Empty, "\uE9D9"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Development, string.Empty, "\uE943"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Settings, string.Empty, "\uE713"));
        RefreshShellNavigationText();
    }

    private void RegisterShellKeyboardAccelerators()
    {
        var shortcuts = new (VirtualKey Key, ShellPageKind Page)[]
        {
            (VirtualKey.Number1, ShellPageKind.Manage),
            (VirtualKey.Number2, ShellPageKind.Create),
            (VirtualKey.Number3, ShellPageKind.Test),
            (VirtualKey.Number4, ShellPageKind.Monitor),
            (VirtualKey.Number5, ShellPageKind.Development),
            (VirtualKey.Number6, ShellPageKind.Settings)
        };

        foreach (var (key, page) in shortcuts)
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = VirtualKeyModifiers.Control
            };
            accelerator.Invoked += (_, args) =>
            {
                SelectShellPage(page);
                args.Handled = true;
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
    }

    private void RefreshShellNavigationText()
    {
        var keys = new Dictionary<ShellPageKind, string>
        {
            [ShellPageKind.Manage] = "Manage",
            [ShellPageKind.Create] = "Edit",
            [ShellPageKind.Test] = "Test",
            [ShellPageKind.Monitor] = "Monitor",
            [ShellPageKind.Development] = "Development",
            [ShellPageKind.Settings] = "Settings"
        };

        foreach (var item in ShellNavigationItems)
        {
            item.Title = ViewModel.Localization[keys[item.Page]];
        }
    }

    private void SelectShellPage(ShellPageKind page, string? editTargetStableId = null)
    {
        var item = ShellNavigationItems.First(candidate => candidate.Page == page);
        _updatingNavigation = true;
        SelectedShellItem = item;
        ShellNavigationList.SelectedItem = item;
        _updatingNavigation = false;
        UpdateShellNavigationAccent();
        UpdateActiveSystemName();

        if (page == ShellPageKind.Manage)
        {
            if (RootFrame.Content is not MainPage)
            {
                RootFrame.Navigate(typeof(MainPage), ViewModel);
            }
            UpdateShellNavigationTextVisibility();
            return;
        }

        if (page == ShellPageKind.Settings)
        {
            if (RootFrame.Content is not SettingsPage)
            {
                RootFrame.Navigate(typeof(SettingsPage), ViewModel);
            }
            UpdateShellNavigationTextVisibility();
            return;
        }

        switch (page)
        {
            case ShellPageKind.Create:
                RootFrame.Navigate(
                    typeof(EditPage),
                    new EditNavigationParameter(ViewModel, editTargetStableId));
                break;
            case ShellPageKind.Test:
                RootFrame.Navigate(typeof(TestPage), ViewModel);
                break;
            case ShellPageKind.Monitor:
                RootFrame.Navigate(typeof(MonitorPage), ViewModel);
                break;
            case ShellPageKind.Development:
                RootFrame.Navigate(typeof(DevelopmentPage), ViewModel);
                break;
        }
        UpdateShellNavigationTextVisibility();
    }

    private void ShellNavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingNavigation && ShellNavigationList.SelectedItem is ShellNavigationItem item)
        {
            SelectShellPage(item.Page);
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShellNavigationTextVisibility();
        UpdateCaptionInset();
        UpdateTitleBarPassthroughRegions();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.IsRealMode))
        {
            SyncModeSwitch();
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.SelectedSystem))
        {
            UpdateActiveSystemName();
        }
    }

    private void UiSettings_ColorValuesChanged(UISettings sender, object args)
    {
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.CurrentPreferences.AccentColor == AccentColorPreference.System)
            {
                ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
            }
            UpdateCaptionButtonColors();
        });
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
        UpdateCaptionButtonColors();
    }

    private void UpdateCaptionButtonColors()
    {
        var foreground = _accessibilitySettings.HighContrast
            ? _uiSettings.GetColorValue(UIColorType.Foreground)
            : RootGrid.ActualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
        var inactiveForeground = Color.FromArgb(0x99, foreground.R, foreground.G, foreground.B);
        var hoverBackground = RootGrid.ActualTheme == ElementTheme.Light
            ? Color.FromArgb(0x16, 0, 0, 0)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        var pressedBackground = RootGrid.ActualTheme == ElementTheme.Light
            ? Color.FromArgb(0x28, 0, 0, 0)
            : Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF);

        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = inactiveForeground;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = hoverBackground;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = pressedBackground;
    }

    private static void SetOwnedColor(string key, Color color)
    {
        var resources = Application.Current.Resources;
        if (resources.ContainsKey(key))
        {
            resources[key] = color;
        }
    }

    private static void SetOwnedBrushColor(string key, Color color)
    {
        var resources = Application.Current.Resources;
        if (resources.ContainsKey(key)
            && resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private void UpdateShellNavigationTextVisibility()
    {
        var compact = RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < 1180;
        foreach (var item in ShellNavigationItems)
        {
            var showText = !compact || item == SelectedShellItem;
            item.TextVisibility = showText ? Visibility.Visible : Visibility.Collapsed;
            item.ItemWidth = showText ? double.NaN : 42;
        }
        ShellNavigationList.InvalidateMeasure();
        ShellNavigationList.ItemsPanelRoot?.InvalidateMeasure();
        UpdateTitleBarPassthroughRegions();
    }

    private void UpdateShellNavigationAccent()
    {
        if (Application.Current.Resources["WinPoolAccentBrush"] is not Brush accent
            || Application.Current.Resources["WinPoolAccentForegroundBrush"] is not Brush accentForeground)
        {
            return;
        }

        var normalForeground = new SolidColorBrush(
            RootGrid.ActualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        WindowTitleText.Foreground = normalForeground;
        ActiveSystemBadge.BorderBrush = accent;
        LocalRealOperationsLabel.Foreground = normalForeground;
        foreach (var item in ShellNavigationItems)
        {
            var selected = item == SelectedShellItem;
            item.Background = selected
                ? accent
                : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            item.Foreground = selected ? accentForeground : normalForeground;
        }
    }

    private void CustomTitleBar_Loaded(object sender, RoutedEventArgs e) =>
        UpdateTitleBarPassthroughRegions();

    private void CustomTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTitleBarPassthroughRegions();

    private void UpdateTitleBarPassthroughRegions()
    {
        if (!ExtendsContentIntoTitleBar || CustomTitleBar.XamlRoot is null)
        {
            return;
        }

        _nonClientPointerSource ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        var scale = CustomTitleBar.XamlRoot.RasterizationScale;
        RectInt32[] regions;
        try
        {
            regions =
            [
                GetPhysicalRect(ShellNavigationList, scale),
                GetPhysicalRect(ModeControls, scale)
            ];
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            return;
        }

        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, regions);
    }

    private static RectInt32 GetPhysicalRect(FrameworkElement element, double scale)
    {
        var bounds = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            Math.Max(1, (int)Math.Round(bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(bounds.Height * scale)));
    }

    private void GlobalNotification_CloseButtonClick(InfoBar sender, object args)
    {
        if (sender.DataContext is GlobalNotification notification)
        {
            NotificationService.Dismiss(notification.Id);
        }
    }

    private void Notifications_CollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (NotificationService.Notifications.Any(x => x.AutoDismiss))
        {
            _notificationDismissTimer.Start();
        }
    }

    private void NotificationDismissTimer_Tick(object? sender, object e)
    {
        var cutoff = DateTimeOffset.Now - TimeSpan.FromSeconds(4);
        var expired = NotificationService.Notifications
            .Where(x => x.AutoDismiss && x.CreatedAt <= cutoff)
            .Select(x => x.Id)
            .ToList();
        foreach (var id in expired)
        {
            NotificationService.Dismiss(id);
        }
        if (!NotificationService.Notifications.Any(x => x.AutoDismiss))
        {
            _notificationDismissTimer.Stop();
        }
    }

    private void LocalRealOperationsWarning_CloseButtonClick(InfoBar sender, object args)
    {
        _realWarningDismissed = true;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R))
             + (0.7152 * Linearize(color.G))
             + (0.0722 * Linearize(color.B));
    }

    private static Color ContrastingText(Color color) =>
        RelativeLuminance(color) > 0.48
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color color, double factor)
    {
        static byte Mix(byte channel, double amount)
        {
            var target = amount >= 0 ? 255d : 0d;
            var value = channel + ((target - channel) * Math.Abs(amount));
            return (byte)Math.Clamp(Math.Round(value), 0, 255);
        }

        return Color.FromArgb(color.A, Mix(color.R, factor), Mix(color.G, factor), Mix(color.B, factor));
    }
}
