using System.Diagnostics;
using System.Drawing;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent;

/// <summary>
/// The tray is a small projection of the Agent's durable state. Its command
/// collection is intentionally fixed so that configuration stays in the main UI.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem welcomeItem;
    private readonly ToolStripMenuItem showMainWindowItem;
    private readonly ToolStripMenuItem pauseTestItem;
    private readonly ToolStripMenuItem pauseMonitoringItem;
    private readonly ToolStripMenuItem exitItem;
    private readonly IUserPreferencesService preferencesService;
    private readonly FileSystemWatcher preferencesWatcher;
    private readonly SynchronizationContext uiContext;
    private AgentSessionCoordinator? coordinator;
    private UserPreferences preferences = new();
    private MonitoringSession? monitoringSession;
    private TestRunId? activeTestRun;
    private string activeTestState = "none";

    public TrayApplicationContext(IUserPreferencesService preferencesService)
    {
        this.preferencesService = preferencesService
            ?? throw new ArgumentNullException(nameof(preferencesService));
        uiContext = SynchronizationContext.Current
            ?? new WindowsFormsSynchronizationContext();

        welcomeItem = new ToolStripMenuItem { Name = "welcome" };
        welcomeItem.Click += (_, _) => OpenApp("Welcome");
        showMainWindowItem = new ToolStripMenuItem { Name = "show-main-window" };
        showMainWindowItem.Click += (_, _) => OpenApp();
        pauseTestItem = new ToolStripMenuItem { Name = "pause-test", Enabled = false };
        pauseTestItem.Click += async (_, _) => await ToggleTestPauseAsync();
        pauseMonitoringItem = new ToolStripMenuItem { Name = "pause-monitoring" };
        pauseMonitoringItem.Click += async (_, _) => await ToggleMonitoringAsync();
        exitItem = new ToolStripMenuItem { Name = "exit" };
        exitItem.Click += async (_, _) => await BeginCompleteExitAsync();

        menu.Items.Add(welcomeItem);
        menu.Items.Add(showMainWindowItem);
        menu.Items.Add(pauseTestItem);
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

        var settingsPath = this.preferencesService is WinPool.Infrastructure.Windows.LocalUserPreferencesService local
            ? local.SettingsPath
            : Path.Combine(WinPool.Infrastructure.Windows.StorageDataLocations.CurrentRoot, "settings.json");
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

    internal void SetTestRun(TestRunId? runId, string state)
    {
        activeTestRun = runId;
        activeTestState = state;
        uiContext.Post(_ => RefreshPresentation(), null);
    }

    internal void ShowSystemSupportRecoveryWarning(int failedCount)
    {
        if (failedCount > 0)
        {
            trayIcon.ShowBalloonTip(8_000, "WinPool",
                $"{failedCount} temporary system state item(s) could not be restored.",
                ToolTipIcon.Warning);
        }
    }

    internal void ShowInterruptedTestRecoveryNotice(int interruptedRunCount)
    {
        if (interruptedRunCount > 0)
        {
            trayIcon.ShowBalloonTip(8_000, "WinPool",
                $"{interruptedRunCount} interrupted test run(s) were preserved.",
                ToolTipIcon.Warning);
        }
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
                var loaded = await preferencesService.LoadAsync();
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

        var testCanPause = activeTestRun is not null
            && activeTestState is "paused" or "pausing" or "running" or "starting";
        pauseTestItem.Text = activeTestState is "paused"
            ? zh ? "恢复测试" : "Resume test"
            : zh ? "暂停测试" : "Pause test";
        pauseTestItem.Enabled = testCanPause && coordinator?.State == AgentLifecycleState.Running;
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

        var session = monitoringSession;
        if (session is not null && session.State is MonitoringSessionState.Starting
            or MonitoringSessionState.Running or MonitoringSessionState.Stopping)
        {
            await coordinator.HandleAsync(new StopAgentMonitoringRequest(
                session.SessionId, CorrelationId.New()));
            await preferencesService.SaveAsync(preferences with { ContinuousMonitoringEnabled = false });
            return;
        }

        await preferencesService.SaveAsync(preferences with { ContinuousMonitoringEnabled = true });
        var systemId = SystemId.New();
        var request = new MonitorRequest(
            SessionId.New(), systemId,
            [new MonitorTarget(new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "pdh-wildcard"), "*"),
             new MonitorTarget(new StorageObjectId(systemId, StorageObjectKind.VirtualDisk, "pdh-storage-spaces-wildcard"), "*")],
            [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.ReadBytesPerSecond,
             MonitorMetricKind.WriteBytesPerSecond, MonitorMetricKind.AverageQueueLength],
            TimeSpan.FromSeconds(1 / Math.Clamp(preferences.MonitoringSampleRateHz, 0.2, 20)),
            ContinueWhenUiCloses: true);
        await coordinator.HandleAsync(new StartAgentMonitoringRequest(request, CorrelationId.New()));
    }

    private async Task ToggleTestPauseAsync()
    {
        if (coordinator?.State != AgentLifecycleState.Running || activeTestRun is not { } runId)
        {
            return;
        }

        if (activeTestState == "paused")
        {
            await coordinator.HandleAsync(new ResumeAgentTestRequest(runId, CorrelationId.New()));
        }
        else
        {
            await coordinator.HandleAsync(new PauseAgentTestRequest(runId, CorrelationId.New()));
        }
    }

    private async Task BeginCompleteExitAsync()
    {
        if (coordinator is null || coordinator.State is AgentLifecycleState.ShuttingDown or AgentLifecycleState.Stopped)
        {
            return;
        }
        var snapshot = await coordinator.HandleAsync(new GetAgentSnapshotRequest(CorrelationId.New()));
        var activeRun = (snapshot.Value as AgentSnapshotResponse)?.Snapshot.ActiveTestRunId;
        if (activeRun is not null && MessageBox.Show(
                "A disk test is active. Exit WinPool will cancel it.\n\nExit WinPool?",
                "WinPool — Active test", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        await coordinator.HandleAsync(new RequestAgentShutdownRequest(
            ShutdownReason.TrayExit, activeRun is not null, CorrelationId.New()));
    }

    private static string ResolveMainApplicationPath()
    {
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var besideAgent = parent is null ? string.Empty : Path.Combine(parent.FullName, "WinPool.App.exe");
        return File.Exists(besideAgent) ? besideAgent : Path.Combine(AppContext.BaseDirectory, "WinPool.App.exe");
    }

    private static Icon LoadIcon() => Environment.ProcessPath is { } executablePath
        ? Icon.ExtractAssociatedIcon(executablePath) ?? (Icon)SystemIcons.Application.Clone()
        : (Icon)SystemIcons.Application.Clone();
}
