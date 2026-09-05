using System.Diagnostics;
using System.Drawing;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent;

/// <summary>
/// The tray is a small projection of the Agent's durable state. Its command
/// collection is intentionally fixed so that configuration stays in the main UI.
/// The tray never writes settings; preference changes are routed through the
/// Agent session coordinator like any other typed request.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem welcomeItem;
    private readonly ToolStripMenuItem showMainWindowItem;
    private readonly ToolStripMenuItem pauseMonitoringItem;
    private readonly ToolStripMenuItem exitItem;
    private readonly IUserPreferencesReader userPreferencesReader;
    private readonly IAgentPreferencesReader agentPreferencesReader;
    private readonly FileSystemWatcher preferencesWatcher;
    private readonly SynchronizationContext uiContext;
    private AgentSessionCoordinator? coordinator;
    private UserPreferences preferences = new();
    private MonitoringSession? monitoringSession;

    public TrayApplicationContext(
        IUserPreferencesReader userPreferencesReader,
        IAgentPreferencesReader agentPreferencesReader)
    {
        this.userPreferencesReader = userPreferencesReader
            ?? throw new ArgumentNullException(nameof(userPreferencesReader));
        this.agentPreferencesReader = agentPreferencesReader
            ?? throw new ArgumentNullException(nameof(agentPreferencesReader));
        uiContext = SynchronizationContext.Current
            ?? new WindowsFormsSynchronizationContext();

        welcomeItem = new ToolStripMenuItem { Name = "welcome" };
        welcomeItem.Click += (_, _) =>
            GuardTrayAction("welcome", () => RunSynchronous(() => OpenApp("Welcome")));
        showMainWindowItem = new ToolStripMenuItem { Name = "show-main-window" };
        showMainWindowItem.Click += (_, _) =>
            GuardTrayAction("show-main-window", () => RunSynchronous(() => OpenApp()));
        pauseMonitoringItem = new ToolStripMenuItem { Name = "pause-monitoring" };
        pauseMonitoringItem.Click += (_, _) =>
            GuardTrayAction("pause-monitoring", ToggleMonitoringAsync);
        exitItem = new ToolStripMenuItem { Name = "exit" };
        exitItem.Click += (_, _) => GuardTrayAction("exit", BeginCompleteExitAsync);

        menu.Items.Add(welcomeItem);
        menu.Items.Add(showMainWindowItem);
        menu.Items.Add(pauseMonitoringItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => OpenApp();
        if (!trayIcon.Visible)
        {
            throw new InvalidOperationException("The tray icon could not be made visible.");
        }

        var settingsPath = userPreferencesReader is WinPool.Infrastructure.Windows.LocalUserPreferencesService local
            ? local.SettingsPath
            : Path.Combine(WinPool.Infrastructure.Windows.StorageDataLocations.CurrentRoot, "app-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        preferencesWatcher = new FileSystemWatcher(
            Path.GetDirectoryName(settingsPath)!,
            Path.GetFileName(settingsPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        preferencesWatcher.Changed += (_, _) => ReloadPreferences();
        preferencesWatcher.Created += (_, _) => ReloadPreferences();
        preferencesWatcher.Renamed += (_, _) => ReloadPreferences();
        ReloadPreferences();
        RefreshPresentation();
    }

    internal bool IsTrayVisible => trayIcon.Visible;

    internal void AttachCoordinator(AgentSessionCoordinator value)
    {
        coordinator = value ?? throw new ArgumentNullException(nameof(value));
        uiContext.Post(_ => RefreshPresentation(), null);
    }

    internal void OpenMainApplication(string? page = null) =>
        uiContext.Post(_ => OpenApp(page), null);

    internal void HideTrayIcon() => uiContext.Send(_ => trayIcon.Visible = false, null);

    internal void ExitAgentThread() => uiContext.Post(
        _ =>
        {
            trayIcon.Visible = false;
            ExitThread();
        },
        null);

    internal void SetMonitoringSession(MonitoringSession? session)
    {
        monitoringSession = session;
        uiContext.Post(_ => RefreshPresentation(), null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            preferencesWatcher.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ReloadPreferences()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await userPreferencesReader.LoadAsync();
                preferences = loaded;
                uiContext.Post(_ => RefreshPresentation(), null);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        });
    }

    private static Task RunSynchronous(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Menu click handlers are async void at the WinForms boundary. An
    /// unhandled exception would take down the Agent message loop, so every
    /// tray command runs through this guard.
    /// </summary>
    private static async void GuardTrayAction(
        string name,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"WinPool tray action '{name}' failed: {exception.Message}");
        }
    }

    private void RefreshPresentation()
    {
        var zh = preferences.Language == LanguagePreference.ZhCn
            || preferences.Language == LanguagePreference.SystemDefault
               && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                   .Equals("zh", StringComparison.OrdinalIgnoreCase);
        welcomeItem.Text = zh ? "欢迎" : "Welcome";
        showMainWindowItem.Text = zh ? "显示主界面" : "Show main window";
        var monitoringActive = monitoringSession?.State is MonitoringSessionState.Starting
            or MonitoringSessionState.Running or MonitoringSessionState.Stopping;
        pauseMonitoringItem.Text = monitoringActive
            ? zh ? "暂停监控" : "Pause monitoring"
            : zh ? "恢复监控" : "Resume monitoring";
        pauseMonitoringItem.Enabled = coordinator?.State == AgentLifecycleState.Running;
        exitItem.Text = zh ? "退出 WinPool" : "Exit WinPool";
        trayIcon.Text = coordinator?.State is AgentLifecycleState.Starting
            or AgentLifecycleState.Recovering
            ? zh ? "WinPool — 正在启动" : "WinPool — Starting"
            : monitoringActive
                ? "WinPool — Monitoring"
                : coordinator?.State == AgentLifecycleState.Failed
                    ? "WinPool — Failed"
                    : "WinPool — Ready";
    }

    private void OpenApp(string? page = null)
    {
        if (coordinator?.State is AgentLifecycleState.Stopped or AgentLifecycleState.ShuttingDown)
        {
            return;
        }

        var executable = ResolveMainApplicationPath();
        if (!File.Exists(executable))
        {
            trayIcon.ShowBalloonTip(4_000, "WinPool",
                "The main application is not installed beside the Agent yet.", ToolTipIcon.Warning);
            return;
        }

        var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(page))
        {
            startInfo.ArgumentList.Add("--page");
            startInfo.ArgumentList.Add(page);
        }
        Process.Start(startInfo);
    }

    private async Task ToggleMonitoringAsync()
    {
        if (coordinator?.State != AgentLifecycleState.Running)
        {
            return;
        }

        var agentPreferences = await agentPreferencesReader.LoadAsync();
        var session = monitoringSession;
        var monitoringActive = session is not null && session.State is MonitoringSessionState.Starting
            or MonitoringSessionState.Running or MonitoringSessionState.Stopping;
        var enable = !monitoringActive;
        await coordinator.HandleAsync(new SetAgentPreferenceRequest(
            AgentPreferenceField.ContinuousMonitoringEnabled,
            enable,
            null,
            CorrelationId.New()));
        if (!enable)
        {
            if (session is not null)
            {
                await coordinator.HandleAsync(new StopAgentMonitoringRequest(
                    session.SessionId, CorrelationId.New()));
            }

            return;
        }

        await coordinator.HandleAsync(new StartAgentMonitoringRequest(
            DesktopAgentRuntime.CreateDefaultMonitorRequest(
                agentPreferences.MonitoringSampleRateHz),
            CorrelationId.New()));
    }

    private async Task BeginCompleteExitAsync()
    {
        if (coordinator is null || coordinator.State is AgentLifecycleState.ShuttingDown or AgentLifecycleState.Stopped)
        {
            return;
        }

        await coordinator.HandleAsync(new RequestAgentShutdownRequest(
            ShutdownReason.TrayExit, CorrelationId.New()));
    }

    private static string ResolveMainApplicationPath() =>
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "WinPool.App.exe"));

    private static Icon LoadIcon() => Environment.ProcessPath is { } executablePath
        ? Icon.ExtractAssociatedIcon(executablePath) ?? (Icon)SystemIcons.Application.Clone()
        : (Icon)SystemIcons.Application.Clone();
}
