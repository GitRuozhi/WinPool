using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Core;
using WinPool.Infrastructure.Windows;

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
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly IElevationRestartService _elevationRestartService;
    private InputNonClientPointerSource? _nonClientPointerSource;

    public WorkspaceViewModel ViewModel { get; }

    public IGlobalNotificationService NotificationService { get; }

    public ObservableCollection<ShellNavigationItem> ShellNavigationItems { get; } = [];

    public ShellNavigationItem? SelectedShellItem { get; set; }

    public MainWindow(ApplicationStartupOptions startupOptions)
    {
        NotificationService = new GlobalNotificationService();
        _elevationRestartService = new WindowsElevationRestartService();
        var importExportService = new DesktopExportService();
        ViewModel = new WorkspaceViewModel(
            new WindowsHardwareInventoryProvider(),
            new WindowsPrivilegeService(),
            new LocalUserPreferencesService(),
            importExportService,
            new LocalStorageSystemRepository(),
            new SimulationOperationService(),
            NotificationService,
            new LocalMachineRecordService(),
            new GlobalCommandLogService());
        if (startupOptions.EnterRealModeAfterElevation)
        {
            ViewModel.TrySetExecutionMode(ExecutionMode.Real);
        }

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1440, 900));
        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        BuildShellNavigation();
        RootFrame.Navigate(typeof(MainPage), ViewModel);
        SelectShellPage(ShellPageKind.Manage);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
        ApplyTheme(ViewModel.CurrentPreferences.Theme);
        ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
        RefreshChrome();
        UpdateCaptionInset();
        UpdateCaptionButtonColors();
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
        var language = ViewModel.Localization.Language;
        var suffix = ViewModel.PrivilegeState == PrivilegeState.Administrator
            ? (language == LanguagePreference.ZhCn ? " [管理员]" : " [Administrator]")
            : string.Empty;
        WindowTitleText.Text = $"WinPool{suffix}";
        Title = WindowTitleText.Text;
        AppWindow.Title = WindowTitleText.Text;
        LocalRealOperationsCheckBox.Content = ViewModel.Localization["LocalRealOperations"];
        LocalRealOperationsCheckBox.SetValue(
            AutomationProperties.NameProperty,
            ViewModel.Localization["LocalRealOperations"]);
        ToolTipService.SetToolTip(
            LocalRealOperationsCheckBox,
            ViewModel.CanUseRealMode ? ViewModel.Localization["ExecutionMode"] : ViewModel.Localization["AdminRequired"]);
        LocalRealOperationsCheckBox.IsEnabled = true;
        LocalRealOperationsWarning.Title = ViewModel.Localization["PreviewWarningTitle"];
        LocalRealOperationsWarning.Message = ViewModel.Localization["PreviewWarningMessage"];
        RefreshShellNavigationText();
        UpdateShellNavigationTextVisibility();
        SyncModeSwitch();
    }

    private void UpdateCaptionInset()
    {
        var right = Math.Max(8, AppWindow.TitleBar.RightInset + 8);
        ModeControls.Margin = new Thickness(8, 0, right, 0);
    }

    private async void LocalRealOperationsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingMode)
        {
            return;
        }

        await RequestExecutionModeAsync(
            LocalRealOperationsCheckBox.IsChecked == true
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
        LocalRealOperationsCheckBox.IsChecked = ViewModel.IsRealMode;
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
        var color = _accessibilitySettings.HighContrast
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

        var foreground = RelativeLuminance(color) > 0.48
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255);
        var hover = Color.FromArgb(0x36, color.R, color.G, color.B);
        var pressed = Color.FromArgb(0x58, color.R, color.G, color.B);
        var border = Blend(color, 0.35);

        SetColorResource("WinPoolAccentColor", color);
        SetColorResource("WinPoolAccentHoverColor", hover);
        SetColorResource("WinPoolAccentPressedColor", pressed);
        SetColorResource("WinPoolAccentBorderColor", border);
        SetColorResource("WinPoolAccentForegroundColor", foreground);
        SetColorResource("SystemAccentColor", color);
        SetColorResource("SystemAccentColorLight1", Blend(color, 0.18));
        SetColorResource("SystemAccentColorLight2", Blend(color, 0.35));
        SetColorResource("SystemAccentColorLight3", Blend(color, 0.52));
        SetColorResource("SystemAccentColorDark1", Blend(color, -0.16));
        SetColorResource("SystemAccentColorDark2", Blend(color, -0.30));
        SetColorResource("SystemAccentColorDark3", Blend(color, -0.44));
        SetBrushColor("WinPoolAccentBrush", color);
        SetBrushColor("WinPoolAccentHoverBrush", hover);
        SetBrushColor("WinPoolAccentPressedBrush", pressed);
        SetBrushColor("WinPoolAccentBorderBrush", border);
        SetBrushColor("WinPoolAccentForegroundBrush", foreground);
        SetBrushColor("AccentFillColorDefaultBrush", color);
        SetBrushColor("AccentFillColorSecondaryBrush", Blend(color, -0.08));
        SetBrushColor("AccentFillColorTertiaryBrush", Blend(color, -0.16));
        SetBrushColor("AccentTextFillColorPrimaryBrush", color);
        SetBrushColor("FocusStrokeColorOuterBrush", color);
        SetBrushColor("ListViewItemSelectionIndicatorBrush", color);
        SetBrushColor("ToggleSwitchFillOn", color);
        SetBrushColor("ToggleSwitchFillOnPointerOver", Blend(color, 0.08));
        SetBrushColor("ToggleSwitchFillOnPressed", Blend(color, -0.10));
        SetBrushColor("ToggleSwitchStrokeOn", color);
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

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateCaptionButtonColors();

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

    private static void SetColorResource(string key, Color color)
    {
        Application.Current.Resources[key] = color;
    }

    private static void SetBrushColor(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
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
        var regions = new[]
        {
            GetPhysicalRect(ShellNavigationList, scale),
            GetPhysicalRect(ModeControls, scale)
        };
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
