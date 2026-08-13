using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.Application;
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
            var directory = StorageDataLocations.CurrentRoot;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "last-crash.txt"),
                $"{DateTime.Now:O} [{source}] {exception}\n\n");
        }
        catch
        {
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = StorageDataLocations.CurrentRoot;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-crash.txt"), e.Exception.ToString());
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
        var privilegeState = new WindowsPrivilegeService().Current;
        var startupOptions = ApplicationStartupOptions.Parse(
            Environment.GetCommandLineArgs().Skip(1),
            privilegeState);
        var agentConnection = EnsureAgentConnection();
        StartExitSignalListener();
        Window = new MainWindow(startupOptions, agentConnection);
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
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
        try
        {
            var connection = EnsureAgentConnection();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var result = await connection.ConnectAsync(timeout.Token);
            if (!result.IsSuccess && Window is MainWindow mainWindow)
            {
                DispatcherQueue.TryEnqueue(
                    () => mainWindow.NotificationService.PublishWarning(
                        "WinPool Agent",
                        "托盘 Agent 未连接；后台监控暂不可用。",
                        "agent",
                        "agent-connect-failed"));
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            if (Window is MainWindow mainWindow)
            {
                DispatcherQueue.TryEnqueue(
                    () => mainWindow.NotificationService.PublishWarning(
                        "WinPool Agent",
                        "托盘 Agent 启动失败；后台监控暂不可用。",
                        "agent",
                        "agent-start-failed"));
            }
        }
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
            NamedPipeAgentConnection.DefaultEndpointPath,
            new AgentProcessLauncher(executable));
        return s_agentConnection;
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
