using System.Diagnostics;
using System.Drawing;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem monitoringStatus;
    private readonly ToolStripMenuItem testStatus;
    private readonly ToolStripMenuItem cancelTest;
    private readonly SynchronizationContext uiContext;
    private AgentSessionCoordinator? coordinator;
    private bool shuttingDown;

    public TrayApplicationContext()
    {
        uiContext = SynchronizationContext.Current
            ?? new WindowsFormsSynchronizationContext();
        monitoringStatus = new ToolStripMenuItem("Monitoring: stopped") { Enabled = false };
        testStatus = new ToolStripMenuItem("Test: none") { Enabled = false };
        cancelTest = new ToolStripMenuItem(
            "Cancel test",
            null,
            async (_, _) => await CancelTestAsync())
        {
            Enabled = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open WinPool", null, (_, _) => OpenApp());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(monitoringStatus);
        menu.Items.Add("Start monitoring", null, async (_, _) => await StartMonitoringAsync());
        menu.Items.Add("Stop monitoring", null, async (_, _) => await StopMonitoringAsync());
        menu.Items.Add(testStatus);
        menu.Items.Add(
            new ToolStripMenuItem("Pause: unavailable for current step")
            {
                Enabled = false
            });
        menu.Items.Add(cancelTest);
        menu.Items.Add("Open Test", null, (_, _) => OpenApp("Test"));
        menu.Items.Add("Open Monitor", null, (_, _) => OpenApp("Monitor"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => OpenApp("Settings"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit WinPool", null, async (_, _) => await BeginCompleteExitAsync());

        trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WinPool — Monitoring stopped; no active test",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => OpenApp();

        if (!trayIcon.Visible)
        {
            throw new InvalidOperationException("The tray icon could not be made visible.");
        }
    }

    internal bool IsTrayVisible => trayIcon.Visible;

    internal bool IsShuttingDown => shuttingDown;

    internal void AttachCoordinator(AgentSessionCoordinator value) =>
        coordinator = value ?? throw new ArgumentNullException(nameof(value));

    internal void OpenMainApplication(string? page = null) =>
        uiContext.Post(_ => OpenApp(page), null);

    internal void HideTrayIcon() =>
        uiContext.Send(_ => trayIcon.Visible = false, null);

    internal void ExitAgentThread() =>
        uiContext.Post(
            _ =>
            {
                trayIcon.Visible = false;
                ExitThread();
            },
            null);

    internal void SetMonitoringSession(MonitoringSession? session) =>
        uiContext.Post(
            _ =>
            {
                var running = session?.State is
                    MonitoringSessionState.Starting
                    or MonitoringSessionState.Running
                    or MonitoringSessionState.Stopping;
                monitoringStatus.Text = running
                    ? $"Monitoring: {session!.State.ToString().ToLowerInvariant()}"
                    : "Monitoring: stopped";
                trayIcon.Text = running
                    ? $"WinPool — Monitoring {session!.State.ToString().ToLowerInvariant()}"
                    : "WinPool — Monitoring stopped; no active test";
            },
            null);

    internal void SetTestRun(TestRunId? runId, string state) =>
        uiContext.Post(
            _ =>
            {
                testStatus.Text = runId is null
                    ? "Test: none"
                    : $"Test: {state} ({runId.Value.Value.ToString("N")[..8]})";
                cancelTest.Enabled = runId is not null
                                     && state is "starting" or "running";
                var monitoring = (monitoringStatus.Text ?? "stopped")
                    .Replace("Monitoring:", string.Empty, StringComparison.Ordinal)
                    .Trim();
                trayIcon.Text = runId is null
                    ? $"WinPool — Monitoring {monitoring}; no active test"
                    : $"WinPool — Test {state}; monitoring {monitoring}";
            },
            null);

    internal void ShowSystemSupportRecoveryWarning(int failedCount)
    {
        if (failedCount <= 0)
        {
            return;
        }

        trayIcon.ShowBalloonTip(
            8_000,
            "WinPool",
            $"{failedCount} temporary system state item(s) could not be restored. Open WinPool to review recovery diagnostics.",
            ToolTipIcon.Warning);
    }

    internal void ShowInterruptedTestRecoveryNotice(int interruptedRunCount)
    {
        if (interruptedRunCount <= 0)
        {
            return;
        }

        trayIcon.ShowBalloonTip(
            8_000,
            "WinPool",
            $"{interruptedRunCount} interrupted test run(s) were preserved. Reopen WinPool to review or resume the immutable plan.",
            ToolTipIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OpenApp(string? page = null)
    {
        if (shuttingDown)
        {
            return;
        }

        var executable = ResolveMainApplicationPath();
        if (!File.Exists(executable))
        {
            trayIcon.ShowBalloonTip(
                4_000,
                "WinPool",
                "The main application is not installed beside the Agent yet.",
                ToolTipIcon.Warning);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(page))
        {
            startInfo.ArgumentList.Add("--page");
            startInfo.ArgumentList.Add(page);
        }

        Process.Start(startInfo);
    }

    private static string ResolveMainApplicationPath()
    {
        var parentDirectory = Directory.GetParent(
            AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        var isolatedRuntimeCandidate = parentDirectory is null
            ? string.Empty
            : Path.Combine(parentDirectory.FullName, "WinPool.App.exe");
        if (File.Exists(isolatedRuntimeCandidate))
        {
            return isolatedRuntimeCandidate;
        }

        return Path.Combine(AppContext.BaseDirectory, "WinPool.App.exe");
    }

    private void ShowNotConnected()
    {
        trayIcon.ShowBalloonTip(
            3_000,
            "WinPool",
            "Monitoring control will become available after the Agent coordinator is connected.",
            ToolTipIcon.Info);
    }

    private async Task StartMonitoringAsync()
    {
        if (coordinator is null || shuttingDown)
        {
            ShowNotConnected();
            return;
        }

        var systemId = SystemId.New();
        var request = new MonitorRequest(
            SessionId.New(),
            systemId,
            [
                    new MonitorTarget(
                        new StorageObjectId(
                            systemId,
                            StorageObjectKind.PhysicalDisk,
                            "pdh-wildcard"),
                        "*"),
                    new MonitorTarget(
                        new StorageObjectId(
                            systemId,
                            StorageObjectKind.VirtualDisk,
                            "pdh-storage-spaces-wildcard"),
                        "*")
                ],
            [
                MonitorMetricKind.ActiveTimePercent,
                    MonitorMetricKind.ReadBytesPerSecond,
                    MonitorMetricKind.WriteBytesPerSecond,
                    MonitorMetricKind.AverageQueueLength,
                    MonitorMetricKind.VirtualDiskActiveBytes,
                    MonitorMetricKind.VirtualDiskMissingBytes,
                    MonitorMetricKind.VirtualDiskStaleBytes,
                    MonitorMetricKind.VirtualDiskNeedRegenerationBytes,
                    MonitorMetricKind.VirtualDiskRegeneratingBytes,
                    MonitorMetricKind.VirtualDiskPendingDeletionBytes
            ],
            TimeSpan.FromSeconds(1),
            ContinueWhenUiCloses: true);
        monitoringStatus.Text = "Monitoring: starting";
        var result = await coordinator.HandleAsync(
            new StartAgentMonitoringRequest(request, CorrelationId.New()));
        if (!result.IsSuccess)
        {
            trayIcon.ShowBalloonTip(
                4_000,
                "WinPool",
                "Monitoring could not be started.",
                ToolTipIcon.Warning);
        }
    }

    private async Task StopMonitoringAsync()
    {
        if (coordinator is null || shuttingDown)
        {
            ShowNotConnected();
            return;
        }

        var snapshotResult = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));
        var session = (snapshotResult.Value as AgentSnapshotResponse)?
            .Snapshot.ActiveMonitoringSession;
        if (session is null)
        {
            SetMonitoringSession(null);
            return;
        }

        monitoringStatus.Text = "Monitoring: stopping";
        var result = await coordinator.HandleAsync(
            new StopAgentMonitoringRequest(
                session.SessionId,
                CorrelationId.New()));
        if (!result.IsSuccess)
        {
            trayIcon.ShowBalloonTip(
                4_000,
                "WinPool",
                "Monitoring could not be stopped cleanly.",
                ToolTipIcon.Warning);
        }
    }

    private async Task CancelTestAsync()
    {
        if (coordinator is null || shuttingDown)
        {
            ShowNotConnected();
            return;
        }

        var snapshotResult = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));
        var runId = (snapshotResult.Value as AgentSnapshotResponse)?
            .Snapshot.ActiveTestRunId;
        if (runId is null)
        {
            SetTestRun(null, "none");
            return;
        }

        cancelTest.Enabled = false;
        await coordinator.HandleAsync(
            new CancelAgentTestRequest(
                runId.Value,
                CorrelationId.New()));
    }

    private async Task BeginCompleteExitAsync()
    {
        if (shuttingDown)
        {
            return;
        }

        if (coordinator is null)
        {
            ShowNotConnected();
            return;
        }

        var snapshot = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));
        var activeRun = (snapshot.Value as AgentSnapshotResponse)?
            .Snapshot.ActiveTestRunId;
        if (activeRun is not null)
        {
            var confirmation = MessageBox.Show(
                "A disk test is active. Exit WinPool will cancel the test, wait for evidence to flush, and terminate its supervised process tree if needed.\n\nExit WinPool?",
                "WinPool — Active test",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }
        }

        shuttingDown = true;
        trayIcon.Text = "WinPool — Shutting down";
        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                UserConfirmedActiveTestCancellation: activeRun is not null,
                CorrelationId.New()));
        if (!result.IsSuccess)
        {
            shuttingDown = false;
            trayIcon.Visible = true;
            trayIcon.Text = "WinPool — Shutdown incomplete";
            trayIcon.ShowBalloonTip(
                5_000,
                "WinPool",
                "Complete exit did not finish. WinPool remains visible in the tray.",
                ToolTipIcon.Warning);
        }
    }

    private static Icon LoadIcon()
    {
        var executablePath = Environment.ProcessPath;
        return executablePath is null
            ? (Icon)SystemIcons.Application.Clone()
            : Icon.ExtractAssociatedIcon(executablePath)
              ?? (Icon)SystemIcons.Application.Clone();
    }
}
