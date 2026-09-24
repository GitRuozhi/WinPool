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
using WinPool.Agent.Client;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool_App.Controls;
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
    private const int MaximumVisibleNotificationCards = 3;
    private const double MaximumNotificationCardHeightDip = 200;
    private const double NotificationStackSpacingDip = 6;
    private bool _initialized;
    private bool _workspaceInitializationComplete;
    private bool _startupPreviewDisplayed;
    private Grid? _workspaceStartupOverlay;
    private Grid? _workspaceStartupOverlayHost;
    private TextBlock? _workspaceStartupMessage;
    private bool _updatingMode;
    private bool _updatingNavigation;
    private bool _updatingSystemSelector;
    private bool _suppressWorkspaceStatePersistence;
    private bool _systemSelectorRefreshPending;
    private string? _pendingSystemSelectionId;
    private SystemId? _editorSystemId;
    private long _editorDocumentRevision;
    private DateTimeOffset _editorDocumentUpdatedAt;
    private bool _requestingElevation;
    private bool _closingForElevationHandoff;
    private bool _showingNotificationMessage;
    private readonly ApplicationStartupTarget _startupTarget;
    private readonly bool _enteredRealModeAfterElevation;
    private string _preferredShellPage = "Manage";
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly IElevationRestartService _elevationRestartService;
    private readonly IWorkspaceStateService _workspaceStateService;
    private readonly AgentPreferencesSynchronizer _agentPreferencesSynchronizer;
    private readonly AgentInventorySynchronizer _agentInventorySynchronizer;
    private readonly MonitorAlertObserver _monitorAlertObserver;
    private readonly DispatcherTimer _notificationDismissTimer;
    private readonly DispatcherTimer _notificationExitFallbackTimer;
    private InputNonClientPointerSource? _nonClientPointerSource;
    private WelcomeWindow? _welcomeWindow;

    public WorkspaceViewModel ViewModel { get; }

    public IGlobalNotificationService NotificationService { get; }

    /// <summary>Projects up to three lower-right notification cards.</summary>
    public ObservableCollection<NotificationDisplayItem> VisibleNotifications { get; } = [];

    public ObservableCollection<ShellNavigationItem> ShellNavigationItems { get; } = [];

    public ShellNavigationItem? SelectedShellItem { get; set; }

    /// <summary>Keeps an existing card template alive while its language updates.</summary>
    public sealed class NotificationDisplayItem : INotifyPropertyChanged
    {
        private GlobalNotification _notification;
        private bool _isChinese;
        private bool _isDismissing;
        private readonly TaskCompletionSource<bool> _dismissalAnimationCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public NotificationDisplayItem(GlobalNotification notification, bool isChinese)
        {
            _notification = notification;
            _isChinese = isChinese;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public GlobalNotification Notification => _notification;

        public bool IsChinese => _isChinese;

        public bool IsDismissing => _isDismissing;

        public Task DismissalAnimationCompleted => _dismissalAnimationCompleted.Task;

        public void Update(GlobalNotification notification, bool isChinese)
        {
            if (!Equals(_notification, notification))
            {
                _notification = notification;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notification)));
            }

            if (_isChinese != isChinese)
            {
                _isChinese = isChinese;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChinese)));
            }
        }

        public void BeginDismissal()
        {
            if (_isDismissing)
            {
                return;
            }

            _isDismissing = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDismissing)));
        }

        public void CompleteDismissalAnimation() => _dismissalAnimationCompleted.TrySetResult(true);
    }

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
        _enteredRealModeAfterElevation = startupOptions.EnterRealModeAfterElevation;
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
        // Keep expiry visually close to the advertised 8/20-second lifetimes.
        // This is trivial work: at most three active cards are inspected.
        _notificationDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _notificationDismissTimer.Tick += NotificationDismissTimer_Tick;
        _notificationDismissTimer.Start();
        _notificationExitFallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _notificationExitFallbackTimer.Tick += NotificationExitFallbackTimer_Tick;
        RefreshNotificationSurface();

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
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
        // Persist on selection change after restore is complete. The close-path
        // save alone races process exit; restore itself must not persist the
        // empty startup placeholder over a remembered local object.
        ViewModel.WorkspaceSelectionChanged += ViewModel_WorkspaceSelectionChanged;
        _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        BuildShellNavigation();
        RegisterShellKeyboardAccelerators();
        _agentPreferencesSynchronizer = new AgentPreferencesSynchronizer(
            ViewModel,
            agentConnection,
            DispatcherQueue);
        _agentPreferencesSynchronizer.Start();
        _agentInventorySynchronizer = new AgentInventorySynchronizer(ViewModel, agentConnection, DispatcherQueue);
        _monitorAlertObserver = new MonitorAlertObserver(ViewModel, DispatcherQueue);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        // Hide system-dependent content before the first asynchronous load.
        // The selector stays hidden until a validated preview is available.
        RootFrame.Visibility = Visibility.Collapsed;
        RootFrame.IsHitTestVisible = false;
        ActiveSystemSelector.Visibility = Visibility.Collapsed;
        ActiveSystemSelector.IsEnabled = false;
        ShellNavigationList.IsEnabled = false;
        ViewModel.BeginWorkspacePrepare();
        ShowWorkspaceStartupOverlay(ViewModel.StatusMessage);
        try
        {
            await ViewModel.InitializePreferencesAsync();
            _preferredShellPage = ViewModel.CurrentPreferences.LastActivePage;
            BuildShellNavigation();
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
        UpdateWorkspaceStartupMessage(ViewModel.StatusMessage);
        var workspaceRestored = false;
        try
        {
            _ = _agentInventorySynchronizer.LoadHistoryAsync();
            using var startupPreviewCancellation = new CancellationTokenSource();
            var startupPreviewTask = LoadStartupWorkspacePreviewAsync(startupPreviewCancellation.Token);
            var agentConnectionTask = App.InitialAgentConnectionTask;
            var firstStartupTask = await Task.WhenAny(startupPreviewTask, agentConnectionTask);
            if (firstStartupTask == agentConnectionTask && !startupPreviewTask.IsCompleted)
            {
                // The handshake can finish before catalog loading starts. Give
                // the local readonly preview a short chance before the slower
                // Agent restore is allowed to become the first visible page.
                await Task.WhenAny(startupPreviewTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            }
            var previewDisplayed = false;
            if (startupPreviewTask.IsCompleted)
            {
                var preview = await startupPreviewTask;
                if (preview is not null
                    && ViewModel.ApplyWorkspaceStartupPreview(preview.State, preview.Document))
                {
                    if (SelectedShellItem?.Page is ShellPageKind.StorageStructure or ShellPageKind.DiskPartition)
                    {
                        // Editor pages capture a snapshot at navigation time.
                        SelectShellPage(SelectedShellItem.Page);
                    }

                    RootFrame.Visibility = Visibility.Visible;
                    RootFrame.IsHitTestVisible = false;
                    HideWorkspaceStartupOverlay();
                    _startupPreviewDisplayed = true;
                    UpdateActiveSystemName();
                    previewDisplayed = true;
                }
            }

            if (!previewDisplayed)
            {
                startupPreviewCancellation.Cancel();
            }

            await agentConnectionTask;
            ViewModel.NotifyWorkspaceLoading();
            UpdateWorkspaceStartupMessage(ViewModel.StatusMessage);
            await ViewModel.InitializeAsync();
            workspaceRestored = true;
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
            // The editor pages capture the active snapshot once at
            // navigation. When the workspace finished loading after a startup
            // navigation, re-create the visible editor so the page does not
            // stay on the pre-init snapshot.
            if ((SelectedShellItem?.Page is ShellPageKind.StorageStructure or ShellPageKind.DiskPartition)
                && (!_startupPreviewDisplayed || EditorDocumentChanged()))
            {
                _suppressWorkspaceStatePersistence = true;
                try
                {
                    SelectShellPage(SelectedShellItem.Page);
                }
                finally
                {
                    _suppressWorkspaceStatePersistence = false;
                }
            }

            _workspaceInitializationComplete = workspaceRestored;
            if (workspaceRestored)
            {
                _startupPreviewDisplayed = false;
            }
            HideWorkspaceStartupOverlay();
            RootFrame.Visibility = Visibility.Visible;
            RootFrame.IsHitTestVisible = workspaceRestored || !_startupPreviewDisplayed;
            ShellNavigationList.IsEnabled = true;
            UpdateActiveSystemName();
        }
        if (_enteredRealModeAfterElevation && ViewModel.IsRealMode && ViewModel.CanUseRealMode)
        {
            PublishRealOperationsWarning();
            NotificationService.PublishInfo(
                ViewModel.Localization["ElevationTitle"],
                ViewModel.Localization["ElevationRestarted"],
                "elevation",
                "elevation-restarted");
        }
        ApplyTheme(ViewModel.CurrentPreferences.Theme);
        ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor);
        RefreshChrome();
        UpdateCaptionInset();
        UpdateCaptionButtonColors();
        _monitorAlertObserver.Start();
    }

    private Task<StartupWorkspacePreview?> LoadStartupWorkspacePreviewAsync(
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            if (ViewModel.AgentConnection is null)
            {
                return null;
            }

            try
            {
                var databasePath = Path.Combine(StorageDataLocations.CurrentRoot, "winpool.db");
                var persisted = await new ReadOnlyWorkspaceStartupReader(databasePath)
                    .LoadAsync(cancellationToken);
                if (persisted is null)
                {
                    return null;
                }

                var document = persisted.SimulationDocument is not null
                    ? SimulationDocumentCodec.Decode(persisted.SimulationDocument)
                    : persisted.LocalInventoryDocument is not null
                        ? LocalInventoryDocumentCodec.TryDecodeCached(persisted.LocalInventoryDocument)
                        : null;
                if (document is null
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        document.Id,
                        persisted.State.ActiveDocumentId))
                {
                    return null;
                }

                return new StartupWorkspacePreview(persisted.State, document);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception)
            {
                // An unavailable, unsupported, or damaged preview falls back
                // to the existing Agent catalog restore path.
                return null;
            }
        }, CancellationToken.None);

    private sealed record StartupWorkspacePreview(
        WorkspaceSessionState State,
        StorageSystemDocument Document);

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

    private void ShowWorkspaceStartupOverlay(string message)
    {
        if (RootFrame.Parent is not Grid host)
        {
            return;
        }

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12
        };
        panel.Children.Add(new ProgressRing
        {
            Width = 32,
            Height = 32,
            IsActive = true
        });

        _workspaceStartupMessage = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Text = message
        };
        panel.Children.Add(_workspaceStartupMessage);

        _workspaceStartupOverlay = new Grid
        {
            Background = CustomTitleBar.Background
        };
        _workspaceStartupOverlay.Children.Add(panel);
        _workspaceStartupOverlayHost = host;
        var notificationIndex = host.Children.IndexOf(GlobalNotificationHost);
        host.Children.Insert(
            notificationIndex < 0 ? host.Children.Count : notificationIndex,
            _workspaceStartupOverlay);
    }

    private void UpdateWorkspaceStartupMessage(string message)
    {
        if (_workspaceStartupMessage is not null)
        {
            _workspaceStartupMessage.Text = message;
        }
    }

    private void HideWorkspaceStartupOverlay()
    {
        if (_workspaceStartupOverlay is not null)
        {
            _workspaceStartupOverlayHost?.Children.Remove(_workspaceStartupOverlay);
        }

        _workspaceStartupOverlay = null;
        _workspaceStartupOverlayHost = null;
        _workspaceStartupMessage = null;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        await _monitorAlertObserver.StopAsync();
        App.StopActivationChannel();
        _agentPreferencesSynchronizer.Dispose();
        _agentInventorySynchronizer.Dispose();
        ((System.Collections.Specialized.INotifyCollectionChanged)NotificationService.Notifications)
            .CollectionChanged -= Notifications_CollectionChanged;
        _notificationDismissTimer.Stop();
        _notificationExitFallbackTimer.Stop();
        NotificationService.ClearHistory();

        if (_closingForElevationHandoff)
        {
            // The old Agent owns the ordered monitoring stop, endpoint release
            // and SQLite lease release. The App has already persisted its
            // workspace before it authorized that Agent shutdown.
            ViewModel.Monitoring.Dispose();
            return;
        }

        // Persistence runs before the monitoring detach and each segment owns
        // its failure: the process may not stay alive long, so the cheapest,
        // most important writes happen first and cannot starve each other.
        try
        {
            if (ViewModel.CanPersistWorkspaceUiState)
            {
                await _workspaceStateService.SaveAsync(
                    ViewModel.CaptureUiState((SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString()));
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
        }

        try
        {
            await ViewModel.SetLastActivePageAsync(
                (SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
        }

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
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or ObjectDisposedException)
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
            ApplicationStartupTarget.Edit => ShellPageKind.StorageStructure,
            ApplicationStartupTarget.DiskAndPartition => ShellPageKind.DiskPartition,
            ApplicationStartupTarget.Test => ShellPageKind.Test,
            ApplicationStartupTarget.Monitor => ShellPageKind.Monitor,
            ApplicationStartupTarget.Development => ShellPageKind.Development,
            ApplicationStartupTarget.Settings => ShellPageKind.Settings,
            ApplicationStartupTarget.Hardware => ShellPageKind.Hardware,
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

    public void ShowStorageStructure(string? targetStableId)
    {
        SelectShellPage(ShellPageKind.StorageStructure, targetStableId);
    }

    public void ShowDiskPartition(string? targetStableId = null)
    {
        SelectShellPage(ShellPageKind.DiskPartition, targetStableId);
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
        ContextHelp.Set(
            ActiveSystemSelector,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
                ? "切换当前查看或编辑的本机、导入或模拟存储系统。"
                : "Switch the local, imported, or simulated storage system currently being viewed or edited.");
        LocalRealOperationsSwitch.IsEnabled = true;
        RefreshShellNavigationText();
        UpdateShellNavigationTextVisibility();
        UpdateActiveSystemName();
        SyncModeSwitch();
        RefreshNotificationSurface();
    }

    private void UpdateActiveSystemName()
    {
        if (ActiveSystemSelector.IsDropDownOpen)
        {
            _systemSelectorRefreshPending = true;
            return;
        }

        _systemSelectorRefreshPending = false;
        var system = ViewModel.SelectedSystem;
        _updatingSystemSelector = true;
        try
        {
            ActiveSystemSelector.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var candidate in ViewModel.SystemCatalog.Systems)
            {
                var prefix = candidate.IsLocal
                    ? (ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[本机]" : "[Local]")
                    : (ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]");
                var item = new ComboBoxItem
                {
                    Content = $"{prefix} {candidate.DisplayName}",
                    Tag = candidate.Id,
                    MaxWidth = 280,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                ActiveSystemSelector.Items.Add(item);
                if (candidate.Id.Equals(system?.Id, StringComparison.OrdinalIgnoreCase)) selected = item;
            }
            ActiveSystemSelector.SelectedItem = selected;
            ActiveSystemSelector.Visibility = (!_workspaceInitializationComplete && !_startupPreviewDisplayed)
                || system is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            ActiveSystemSelector.IsEnabled = _workspaceInitializationComplete;
            AutomationProperties.SetName(ActiveSystemSelector,
                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "存储系统" : "Storage system");
        }
        finally
        {
            _updatingSystemSelector = false;
        }
        QueueTitleBarPassthroughRegionUpdate();
    }

    private void ActiveSystemSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSystemSelector
            || ActiveSystemSelector.SelectedItem is not ComboBoxItem { Tag: string systemId }) return;

        if (ActiveSystemSelector.IsDropDownOpen)
        {
            _pendingSystemSelectionId = systemId;
            return;
        }

        ViewModel.SelectSystem(systemId);
    }

    private void ActiveSystemSelector_DropDownClosed(object sender, object e)
    {
        if (_pendingSystemSelectionId is { } systemId)
        {
            _pendingSystemSelectionId = null;
            ViewModel.SelectSystem(systemId);
        }

        if (_systemSelectorRefreshPending)
        {
            UpdateActiveSystemName();
        }
    }

    private void ActiveSystemSelectorHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_accessibilitySettings.HighContrast)
        {
            return;
        }

        ActiveSystemSelectorHost.Background =
            (Brush)Application.Current.Resources["WinPoolTitleBarSelectorHoverBrush"];
    }

    private void ActiveSystemSelectorHost_PointerExited(object sender, PointerRoutedEventArgs e) =>
        ActiveSystemSelectorHost.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

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

    /// <summary>
    /// Requests the execution mode change. Returns <see langword="true"/> only
    /// when this window has handed off to an elevated replacement and is closing.
    /// Callers must not update this window after that handoff.
    /// </summary>
    public async Task<bool> RequestExecutionModeAsync(ExecutionMode requestedMode)
    {
        if (requestedMode == ExecutionMode.Simulation)
        {
            ViewModel.TrySetExecutionMode(ExecutionMode.Simulation);
            SyncModeSwitch();
            return false;
        }

        SyncModeSwitch();
        if (_requestingElevation || RootGrid.XamlRoot is null)
        {
            return false;
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
            var result = await DialogCoordinator.ShowAsync(dialog, RootGrid.XamlRoot);
            if (result != ContentDialogResult.Primary)
            {
                return false;
            }

            if (ViewModel.CanUseRealMode)
            {
                ViewModel.TrySetExecutionMode(ExecutionMode.Real);
                PublishRealOperationsWarning();
                return false;
            }

            var agentProcess = await GetCurrentAgentProcessForElevationAsync(localization);
            if (agentProcess is null)
            {
                return false;
            }

            var restart = await _elevationRestartService.RestartElevatedAsync(
                ApplicationStartupOptions.ElevatedRealArgument,
                agentProcess);
            if (restart.Status == ElevationRestartStatus.Started)
            {
                if (restart.Handoff is null
                    || !await PersistWorkspaceForElevationHandoffAsync(localization))
                {
                    return false;
                }

                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var shutdown = await ViewModel.AgentConnection!.SendAsync(
                    new RequestAgentShutdownRequest(
                        ShutdownReason.ElevationRestart,
                        CorrelationId.New(),
                        BeginInBackground: true),
                    shutdownTimeout.Token);
                if (!shutdown.IsSuccess || shutdown.Value is not AgentAcknowledgement)
                {
                    NotificationService.PublishError(
                        localization["Error"],
                        localization["ElevationAgentShutdownFailed"],
                        "elevation",
                        $"elevation-agent-shutdown:{DateTimeOffset.UtcNow.Ticks}");
                    return false;
                }

                // The Agent has accepted a background, ordered shutdown. Only
                // now may the elevated bootstrap leave its waiting stage. If
                // that signal cannot be sent, it times out without opening a
                // business window; this window retains the explanatory error.
                if (!TryContinueElevatedHandoff(restart.Handoff))
                {
                    NotificationService.PublishError(
                        localization["Error"],
                        localization["ElevationHandoffFailed"],
                        "elevation",
                        $"elevation-continuation:{DateTimeOffset.UtcNow.Ticks}");
                    return false;
                }

                _closingForElevationHandoff = true;
                _welcomeWindow?.Close();
                Close();
                return true;
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
            if (!_closingForElevationHandoff)
            {
                SyncModeSwitch();
            }
        }

        return false;
    }

    private async Task<ProcessHandoffWitness?> GetCurrentAgentProcessForElevationAsync(
        LocalizationService localization)
    {
        if (ViewModel.AgentConnection is not NamedPipeAgentConnection connection)
        {
            NotificationService.PublishError(
                localization["Error"],
                localization["ElevationAgentUnavailable"],
                "elevation",
                $"elevation-agent-unavailable:{DateTimeOffset.UtcNow.Ticks}");
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var connected = await connection.ConnectAsync(timeout.Token);
            var handshake = connection.ActiveHandshake ?? connected.Value;
            if (!connected.IsSuccess
                || handshake is null
                || handshake.ProcessId <= 0
                || handshake.StartedAtUtc == default)
            {
                NotificationService.PublishError(
                    localization["Error"],
                    localization["ElevationAgentUnavailable"],
                    "elevation",
                    $"elevation-agent-unavailable:{DateTimeOffset.UtcNow.Ticks}");
                return null;
            }

            return new ProcessHandoffWitness(
                handshake.ProcessId,
                handshake.StartedAtUtc);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {
            NotificationService.PublishError(
                localization["Error"],
                localization["ElevationAgentUnavailable"],
                "elevation",
                $"elevation-agent-unavailable:{DateTimeOffset.UtcNow.Ticks}");
            return null;
        }
    }

    private async Task<bool> PersistWorkspaceForElevationHandoffAsync(
        LocalizationService localization)
    {
        try
        {
            if (ViewModel.CanPersistWorkspaceUiState)
            {
                await _workspaceStateService.SaveAsync(
                    ViewModel.CaptureUiState((SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString()));
            }

            await ViewModel.SetLastActivePageAsync(
                (SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString());
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            NotificationService.PublishError(
                localization["Error"],
                localization["ElevationHandoffFailed"],
                "elevation",
                $"elevation-workspace-save:{DateTimeOffset.UtcNow.Ticks}");
            return false;
        }
    }

    private static bool TryContinueElevatedHandoff(ElevationHandoff handoff)
    {
        try
        {
            using var continuation = EventWaitHandle.OpenExisting(handoff.ContinuationEventName);
            return continuation.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SyncModeSwitch()
    {
        _updatingMode = true;
        LocalRealOperationsSwitch.IsOn = ViewModel.IsRealMode;
        _updatingMode = false;
    }

    private void PublishRealOperationsWarning()
    {
        NotificationService.PublishWarning(
            ViewModel.Localization["PreviewWarningTitle"],
            ViewModel.Localization["PreviewWarningMessage"],
            "execution-mode",
            $"execution.real-mode:{DateTimeOffset.UtcNow.UtcTicks}",
            options: new GlobalNotificationOptions { Code = "execution.real-mode" });
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
        var titleBarSelectorHover = Color.FromArgb(0x66, color.R, color.G, color.B);
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
        SetOwnedColor("WinPoolTitleBarSelectorHoverColor", titleBarSelectorHover);
        SetOwnedColor("WinPoolAccentBorderColor", fill);
        SetOwnedColor("WinPoolAccentForegroundColor", foreground);
        SetOwnedBrushColor("WinPoolAccentBrush", color);
        SetOwnedBrushColor("WinPoolAccentHoverBrush", hover);
        SetOwnedBrushColor("WinPoolAccentPressedBrush", pressed);
        SetOwnedBrushColor("WinPoolTitleBarSelectorHoverBrush", titleBarSelectorHover);
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
        _updatingNavigation = true;
        ShellNavigationItems.Clear();
        if (ViewModel.CurrentPreferences.DeveloperMode)
        {
            ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Hardware, string.Empty, "\uE950"));
        }
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Manage, string.Empty, "\uE80F"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.StorageStructure, string.Empty, "\uE710"));
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.DiskPartition, string.Empty, "\uEDA2"));
        if (ViewModel.CurrentPreferences.DeveloperMode)
        {
            ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Test, string.Empty, "\uE768"));
        }
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Monitor, string.Empty, "\uE9D9"));
        if (ViewModel.CurrentPreferences.DeveloperMode)
        {
            ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Development, string.Empty, "\uE943"));
        }
        ShellNavigationItems.Add(new ShellNavigationItem(ShellPageKind.Settings, string.Empty, "\uE713"));
        _updatingNavigation = false;
        RefreshShellNavigationText();
    }

    private static bool IsDeveloperPage(ShellPageKind page) =>
        page is ShellPageKind.Hardware or ShellPageKind.Test or ShellPageKind.Development;

    private bool IsShellPageAvailable(ShellPageKind page) =>
        !IsDeveloperPage(page) || ViewModel.CurrentPreferences.DeveloperMode;

    private void RefreshDeveloperNavigation()
    {
        var selectedPage = SelectedShellItem?.Page ?? ShellPageKind.Manage;
        BuildShellNavigation();
        SelectShellPage(IsShellPageAvailable(selectedPage) ? selectedPage : ShellPageKind.Manage);
    }

    private void RegisterShellKeyboardAccelerators()
    {
        var shortcuts = new (VirtualKey Key, ShellPageKind Page)[]
        {
            (VirtualKey.Number1, ShellPageKind.Manage),
            (VirtualKey.Number2, ShellPageKind.StorageStructure),
            (VirtualKey.Number3, ShellPageKind.DiskPartition),
            (VirtualKey.Number4, ShellPageKind.Test),
            (VirtualKey.Number5, ShellPageKind.Monitor),
            (VirtualKey.Number6, ShellPageKind.Development),
            (VirtualKey.Number7, ShellPageKind.Settings),
            (VirtualKey.Number8, ShellPageKind.Hardware)
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
            [ShellPageKind.Hardware] = "Hardware",
            [ShellPageKind.StorageStructure] = "StorageStructure",
            [ShellPageKind.DiskPartition] = "DiskPartition",
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

    private void SelectShellPage(ShellPageKind page, string? editorTargetStableId = null)
    {
        if (!IsShellPageAvailable(page))
        {
            page = ShellPageKind.Manage;
            editorTargetStableId = null;
        }
        var item = ShellNavigationItems.First(candidate => candidate.Page == page);
        _updatingNavigation = true;
        SelectedShellItem = item;
        ShellNavigationList.SelectedItem = item;
        _updatingNavigation = false;
        UpdateShellNavigationAccent();
        UpdateActiveSystemName();
        PersistLastActivePage(page);
        PersistWorkspaceState();

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
            case ShellPageKind.Hardware:
                RootFrame.Navigate(typeof(HardwarePage), ViewModel);
                break;
            case ShellPageKind.StorageStructure:
                if (RootFrame.Navigate(
                    typeof(StorageStructurePage),
                    new EditorNavigationParameter(ViewModel, editorTargetStableId)))
                {
                    _editorSystemId = ViewModel.SelectedSystem.SystemId;
                    _editorDocumentRevision = ViewModel.SelectedSystem.Revision;
                    _editorDocumentUpdatedAt = ViewModel.SelectedSystem.UpdatedAt;
                }
                break;
            case ShellPageKind.DiskPartition:
                if (RootFrame.Navigate(
                    typeof(DiskPartitionPage),
                    new EditorNavigationParameter(ViewModel, editorTargetStableId)))
                {
                    _editorSystemId = ViewModel.SelectedSystem.SystemId;
                    _editorDocumentRevision = ViewModel.SelectedSystem.Revision;
                    _editorDocumentUpdatedAt = ViewModel.SelectedSystem.UpdatedAt;
                }
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
        QueueTitleBarPassthroughRegionUpdate();
        RefreshNotificationSurface();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.IsRealMode))
        {
            SyncModeSwitch();
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.SelectedSystem))
        {
            // The editor pages bind the active system snapshot on navigation.
            // Defer until the ComboBox has closed. Same-system commits also
            // raise this event; only an actual identity change needs navigation.
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateActiveSystemName();
                RefreshSelectedSystemEditor();
            });
            PersistWorkspaceState();
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.CurrentPreferences))
        {
            RefreshDeveloperNavigation();
        }
    }

    private void RefreshSelectedSystemEditor()
    {
        if (SelectedShellItem?.Page is not (ShellPageKind.StorageStructure or ShellPageKind.DiskPartition))
        {
            return;
        }

        // The editors already refresh their own content after a commit.
        // Re-navigating for a new revision of the same system replaces the
        // entire page, flashes, and discards its selection and scroll state.
        // Compare at dispatch time so queued duplicate notifications coalesce.
        if (_editorSystemId == ViewModel.SelectedSystem.SystemId)
        {
            return;
        }

        // Do not carry a stable ID from the former system into the newly
        // selected system. The editor resolves its normal initial selection.
        SelectShellPage(SelectedShellItem.Page);
    }

    private bool EditorDocumentChanged() =>
        _editorSystemId != ViewModel.SelectedSystem.SystemId
        || _editorDocumentRevision != ViewModel.SelectedSystem.Revision
        || _editorDocumentUpdatedAt != ViewModel.SelectedSystem.UpdatedAt;

    private void ViewModel_WorkspaceSelectionChanged(object? sender, EventArgs e) =>
        PersistWorkspaceState();

    /// <summary>
    /// Saves-on-change run fire-and-forget on UI-driven triggers. The awaited
    /// close-time fallback in MainWindow_Closed remains the safety net.
    /// </summary>
    private static async void FireAndForget(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
        }
    }

    private void PersistWorkspaceState()
    {
        if (_suppressWorkspaceStatePersistence || !ViewModel.CanPersistWorkspaceUiState)
        {
            return;
        }

        FireAndForget(() => _workspaceStateService.SaveAsync(
            ViewModel.CaptureUiState((SelectedShellItem?.Page ?? ShellPageKind.Manage).ToString())));
    }

    private void PersistLastActivePage(ShellPageKind page) =>
        FireAndForget(() => ViewModel.SetLastActivePageAsync(page.ToString()));

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
        var compactThreshold = ViewModel.Localization.IsChinese ? 1180 : 1500;
        var compact = RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < compactThreshold;
        foreach (var item in ShellNavigationItems)
        {
            var showText = !compact || item == SelectedShellItem;
            item.TextVisibility = showText ? Visibility.Visible : Visibility.Collapsed;
            item.ItemWidth = showText ? double.NaN : 42;
        }
        ShellNavigationList.InvalidateMeasure();
        ShellNavigationList.ItemsPanelRoot?.InvalidateMeasure();
        QueueTitleBarPassthroughRegionUpdate();
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
        ActiveSystemSelector.BorderBrush = accent;
        ActiveSystemSelectorHost.BorderBrush = accent;
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
        QueueTitleBarPassthroughRegionUpdate();

    private void CustomTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueTitleBarPassthroughRegionUpdate();

    private void TitleBarInteractiveElement_Loaded(object sender, RoutedEventArgs e) =>
        QueueTitleBarPassthroughRegionUpdate();

    private void TitleBarInteractiveElement_SizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueTitleBarPassthroughRegionUpdate();

    private void QueueTitleBarPassthroughRegionUpdate() =>
        DispatcherQueue.TryEnqueue(UpdateTitleBarPassthroughRegions);

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
            var elements = new List<FrameworkElement> { ShellNavigationList, ModeControls };
            if (ActiveSystemSelector.Visibility == Visibility.Visible)
            {
                elements.Add(ActiveSystemSelectorHost);
            }
            regions = elements.Select(element => GetPhysicalRect(element, scale)).ToArray();
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

    private void Notifications_CollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshNotificationSurface();
    }

    private void NotificationDismissTimer_Tick(object? sender, object e)
    {
        // The service owns duration and lifecycle resolution. The shell never
        // derives expiry from timestamps.
        NotificationService.DismissExpired();
        RefreshNotificationSurface();
    }

    private void RefreshNotificationSurface()
    {
        var visible = NotificationService.Notifications
            .Take(GetMaximumVisibleNotificationCards())
            .ToArray();
        var isChinese = ViewModel.Localization.IsChinese;

        var visibleIds = visible
            .Select(notification => notification.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in VisibleNotifications.ToArray())
        {
            if (visibleIds.Contains(item.Notification.Id))
            {
                if (!item.IsDismissing)
                {
                    var updated = visible.First(notification =>
                        notification.Id.Equals(item.Notification.Id, StringComparison.Ordinal));
                    item.Update(updated, isChinese);
                }
                // If the same ID returns while its old view is leaving, keep
                // that view bound to its original record until completion.
                // The post-exit sync creates a fresh view from the latest snapshot.
            }
            else if (!item.IsDismissing)
            {
                // Storyboard.Completed is the normal removal signal. This
                // bounded fallback also releases the queue if that event is
                // not delivered.
                _notificationExitFallbackTimer.Stop();
                _notificationExitFallbackTimer.Start();
                item.BeginDismissal();
            }
        }

        // Hold each departed item until its rightward exit has completed. New
        // cards fill the freed slot afterward, keeping the visible stack within
        // its current window capacity when an eviction and publish coincide.
        if (VisibleNotifications.Any(item => item.IsDismissing))
        {
            return;
        }
        _notificationExitFallbackTimer.Stop();

        for (var desiredIndex = 0; desiredIndex < visible.Length; desiredIndex++)
        {
            var currentIndex = -1;
            for (var index = desiredIndex; index < VisibleNotifications.Count; index++)
            {
                if (VisibleNotifications[index].Notification.Id.Equals(
                        visible[desiredIndex].Id,
                        StringComparison.Ordinal))
                {
                    currentIndex = index;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                VisibleNotifications.Insert(
                    desiredIndex,
                    new NotificationDisplayItem(visible[desiredIndex], isChinese));
                continue;
            }

            if (currentIndex != desiredIndex)
            {
                VisibleNotifications.Move(currentIndex, desiredIndex);
            }

            VisibleNotifications[desiredIndex].Update(visible[desiredIndex], isChinese);
        }
        while (VisibleNotifications.Count > visible.Length)
        {
            VisibleNotifications.RemoveAt(VisibleNotifications.Count - 1);
        }
    }

    private int GetMaximumVisibleNotificationCards()
    {
        var availableHeight = Math.Max(
            0,
            RootGrid.ActualHeight
                - CustomTitleBar.ActualHeight
                - GlobalNotificationHost.Margin.Top
                - GlobalNotificationHost.Margin.Bottom);
        var count = (int)Math.Floor(
            (availableHeight + NotificationStackSpacingDip)
            / (MaximumNotificationCardHeightDip + NotificationStackSpacingDip));
        return Math.Clamp(count, 1, MaximumVisibleNotificationCards);
    }

    private void NotificationCard_ExitAnimationCompleted(object? sender, EventArgs e)
    {
        // Use the display-item identity so a delayed completion from an old
        // card cannot remove a new card that happens to carry the same ID.
        if (sender is not NotificationCard card
            || card.DataContext is not NotificationDisplayItem item
            || !item.IsDismissing
            || !VisibleNotifications.Contains(item))
        {
            return;
        }

        CompleteNotificationExit(item);
    }

    private void NotificationExitFallbackTimer_Tick(object? sender, object e)
    {
        _notificationExitFallbackTimer.Stop();
        foreach (var item in VisibleNotifications.Where(candidate => candidate.IsDismissing).ToArray())
        {
            CompleteNotificationExit(item);
        }
    }

    private void CompleteNotificationExit(NotificationDisplayItem item)
    {
        if (!item.IsDismissing || !VisibleNotifications.Remove(item))
        {
            return;
        }

        item.CompleteDismissalAnimation();
        if (!VisibleNotifications.Any(candidate => candidate.IsDismissing))
        {
            _notificationExitFallbackTimer.Stop();
        }

        RefreshNotificationSurface();
    }

    private async void NotificationCard_Invoked(
        object? sender,
        NotificationCardEventArgs e)
    {
        if (e.Notification.Severity != GlobalNotificationSeverity.Error)
        {
            NotificationService.Dismiss(e.Notification.Id);
            return;
        }

        if (_showingNotificationMessage || RootGrid.XamlRoot is null)
        {
            return;
        }

        _showingNotificationMessage = true;
        // Remove first so repeated taps cannot enqueue the same dialog behind
        // itself. The retained history remains the source of full metadata.
        var displayItem = VisibleNotifications.FirstOrDefault(item =>
            item.Notification.Id.Equals(e.Notification.Id, StringComparison.Ordinal));
        NotificationService.Dismiss(e.Notification.Id);
        try
        {
            if (displayItem is not null)
            {
                try
                {
                    await displayItem.DismissalAnimationCompleted.WaitAsync(TimeSpan.FromMilliseconds(500));
                }
                catch (TimeoutException)
                {
                    // If the visual tree closes during its exit, still show
                    // the full error message instead of waiting indefinitely.
                    if (displayItem.IsDismissing)
                    {
                        CompleteNotificationExit(displayItem);
                    }
                }
            }

            await ShowErrorNotificationMessageAsync(e.Notification);
        }
        catch (Exception exception)
        {
            // A window can close while this dialog is waiting behind another
            // same-window dialog. Do not turn that display failure into a new
            // notification (which could recursively request the same dialog).
            try
            {
                DiagnosticLog.AppendFailure(
                    StorageDataLocations.CurrentRoot,
                    "notification-dialog.jsonl",
                    "ShowErrorNotification",
                    exception);
            }
            catch (Exception loggingException) when (
                loggingException is IOException or UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            _showingNotificationMessage = false;
        }
    }

    private async Task ShowErrorNotificationMessageAsync(GlobalNotification notification)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var zh = ViewModel.Localization.IsChinese;
        var message = new TextBox
        {
            AcceptsReturn = true,
            Height = 220,
            IsReadOnly = true,
            IsSpellCheckEnabled = false,
            Text = notification.Message,
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            Title = notification.Title,
            Content = message,
            CloseButtonText = zh ? "关闭" : "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = RootGrid.RequestedTheme
        };
        await DialogCoordinator.ShowAsync(dialog, RootGrid.XamlRoot);
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
