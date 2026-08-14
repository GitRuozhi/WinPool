using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.Application;
using WinPool.App.Services;
using WinPool.Infrastructure.Windows;
using WinPool.Agent.Client;
using WinPool.Ipc;
using System.Security.Principal;
using System.Runtime.InteropServices;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinPool_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static ApplicationStartupTarget? s_activationTarget;
    private static NamedPipeAgentConnection? s_agentConnection;
    private static EventWaitHandle? s_exitSignal;
    private static CancellationTokenSource? s_activationChannelCts;

    internal static bool InitialAgentWarningPublished { get; private set; }

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        WriteCrashLog("AppDomain", e.ExceptionObject as Exception);

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("UnobservedTask", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            DiagnosticLog.AppendFailure(
                StorageDataLocations.CurrentRoot,
                "app-crash.jsonl",
                source,
                exception);
        }
        catch
        {
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            DiagnosticLog.AppendFailure(
                StorageDataLocations.CurrentRoot,
                "app-crash.jsonl",
                "XamlUnhandled",
                e.Exception);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        InitialAgentWarningPublished = false;
        var privilegeState = new WindowsPrivilegeService().Current;
        var startupOptions = ApplicationStartupOptions.Parse(
            Environment.GetCommandLineArgs().Skip(1),
            privilegeState);
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (startupOptions.Target == ApplicationStartupTarget.Welcome)
        {
            Window = new WelcomeWindow(new LocalizationService());
            Window.Activate();
            return;
        }

        var agentConnection = EnsureAgentConnection();
        StartExitSignalListener();
        Window = new MainWindow(startupOptions, agentConnection);
        Window.Activate();
        if (startupOptions.Target == ApplicationStartupTarget.None
            && !IsTrayAgentRunning())
        {
            ((MainWindow)Window).ShowStartupWelcome();
        }
        StartActivationChannel();
        _ = ConnectAgentAsync();
        if (s_activationTarget is not null)
        {
            RequestMainWindowActivation(s_activationTarget);
        }
    }

    internal static ApplicationStartupTarget? ParseActivationTarget(
        Microsoft.Windows.AppLifecycle.AppActivationArguments arguments)
    {
        var data = arguments.Data;
        var argumentText = data?
            .GetType()
            .GetProperty("Arguments")?
            .GetValue(data) as string;
        if (string.IsNullOrWhiteSpace(argumentText))
        {
            argumentText = data?
                .GetType()
                .GetProperty("Data")?
                .GetValue(data) as string;
        }
        if (string.IsNullOrWhiteSpace(argumentText)
            && data is Windows.ApplicationModel.Activation.CommandLineActivatedEventArgs commandLine)
        {
            argumentText = commandLine.Operation?.Arguments;
        }
        if (string.IsNullOrWhiteSpace(argumentText)
            && data is Windows.ApplicationModel.Activation.LaunchActivatedEventArgs launch)
        {
            argumentText = launch.Arguments;
        }
        if (string.IsNullOrWhiteSpace(argumentText))
        {
            return null;
        }

        var target = ApplicationStartupOptions.ParseTarget(
            argumentText.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return target == ApplicationStartupTarget.None ? null : target;
    }

    private static async Task ConnectAgentAsync()
    {
        const int attemptSeconds = 12;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        try
        {
            var connection = EnsureAgentConnection();
            while (DateTimeOffset.UtcNow < deadline)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(attemptSeconds));
                var result = await connection.ConnectAsync(timeout.Token);
                if (result.IsSuccess)
                {
                    return;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(500)
                        ? remaining
                        : TimeSpan.FromMilliseconds(500));
            }

            PublishInitialAgentWarning(
                "托盘 Agent 在启动恢复期后仍未连接；后台监控暂不可用。",
                "agent-connect-failed");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            PublishInitialAgentWarning(
                "托盘 Agent 启动失败；后台监控暂不可用。",
                "agent-start-failed");
        }
    }

    private static void PublishInitialAgentWarning(string message, string occurrenceKey)
    {
        if (InitialAgentWarningPublished || Window is not MainWindow mainWindow)
        {
            return;
        }

        InitialAgentWarningPublished = true;
        DispatcherQueue.TryEnqueue(
            () => mainWindow.NotificationService.PublishWarning(
                "WinPool Agent",
                message,
                "agent",
                occurrenceKey));
    }

    private static void StartActivationChannel()
    {
        if (s_activationChannelCts is not null)
        {
            return;
        }

        s_activationChannelCts = new CancellationTokenSource();
        var cancellationToken = s_activationChannelCts.Token;
        _ = Task.Run(
            () => ApplicationActivationChannel.ListenAsync(
                cancellationToken,
                target =>
                {
                    RequestMainWindowActivation(target);
                    return Task.CompletedTask;
                }));
    }

    internal static void StopActivationChannel()
    {
        s_activationChannelCts?.Cancel();
        s_activationChannelCts?.Dispose();
        s_activationChannelCts = null;
    }

    private static NamedPipeAgentConnection EnsureAgentConnection()
    {
        if (s_agentConnection is not null)
        {
            return s_agentConnection;
        }

        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "Agent",
            "WinPool.Agent.exe");
        s_agentConnection = new NamedPipeAgentConnection(
            DataRootLayout.AgentEndpointPath(StorageDataLocations.CurrentRoot),
            new AgentProcessLauncher(executable));
        return s_agentConnection;
    }

    private static bool IsTrayAgentRunning()
    {
        try
        {
            return File.Exists(
                DataRootLayout.AgentEndpointPath(
                    StorageDataLocations.CurrentRoot))
                || System.Diagnostics.Process.GetProcessesByName(
                    "WinPool.Agent").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void StartExitSignalListener()
    {
        if (s_exitSignal is not null)
        {
            return;
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return;
        }

        s_exitSignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            AppExitSignal.CreateName(IpcIdentity.HashUserSid(sid)));
        _ = Task.Run(
            () =>
            {
                s_exitSignal.WaitOne();
                DispatcherQueue.TryEnqueue(() => Window?.Close());
            });
    }

    internal static void RequestMainWindowActivation(
        ApplicationStartupTarget? target = null)
    {
        if (Window is null || DispatcherQueue is null)
        {
            s_activationTarget = target;
            return;
        }

        s_activationTarget = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (target is not null && Window is MainWindow mainWindow)
            {
                if (target == ApplicationStartupTarget.Welcome)
                {
                    mainWindow.ShowWelcome();
                    return;
                }

                mainWindow.ActivateTarget(target.Value);
            }
            Window.Activate();
            if (WindowHandle != nint.Zero)
            {
                ShowWindow(WindowHandle, 9);
                SetForegroundWindow(WindowHandle);
            }
        });
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
