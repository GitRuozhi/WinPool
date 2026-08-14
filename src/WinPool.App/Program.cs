using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinPool.Application;

namespace WinPool_App;

public static class Program
{
    private const string SingleInstanceKey = "WinPool.SingleInstance";
    private static nint s_redirectEventHandle;

    [STAThread]
    public static int Main(string[] args)
    {
        WaitForProcessHandoff(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection(args))
        {
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static bool DecideRedirection(IReadOnlyList<string> args)
    {
        if (ApplicationStartupOptions.ParseTarget(args) == ApplicationStartupTarget.Welcome)
        {
            return false;
        }

        var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return false;
        }

        var target = ApplicationStartupOptions.ParseTarget(args);
        if (target != ApplicationStartupTarget.None
            && ApplicationActivationChannel.TrySend(target))
        {
            if (target != ApplicationStartupTarget.Welcome)
            {
                BringExistingProcessToForeground(keyInstance);
            }
            return true;
        }

        RedirectActivationTo(activationArguments, keyInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args) =>
        App.RequestMainWindowActivation(App.ParseActivationTarget(args));

    private static void RedirectActivationTo(
        AppActivationArguments arguments,
        AppInstance keyInstance)
    {
        s_redirectEventHandle = CreateEvent(nint.Zero, true, false, null);
        Task.Run(() =>
        {
            try
            {
                keyInstance.RedirectActivationToAsync(arguments).AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                SetEvent(s_redirectEventHandle);
            }
        });

        _ = CoWaitForMultipleObjects(
            0,
            10_000,
            1,
            [s_redirectEventHandle],
            out _);

        BringExistingProcessToForeground(keyInstance);
        CloseHandle(s_redirectEventHandle);
        s_redirectEventHandle = nint.Zero;
    }

    private static void BringExistingProcessToForeground(AppInstance keyInstance)
    {
        try
        {
            using var process = Process.GetProcessById((int)keyInstance.ProcessId);
            if (process.MainWindowHandle != nint.Zero)
            {
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static void WaitForProcessHandoff(IReadOnlyList<string> arguments)
    {
        if (!ApplicationStartupOptions.RequestsProcessHandoff(arguments))
        {
            return;
        }

        var processId = ApplicationStartupOptions.GetHandoffProcessId(arguments);
        if (processId is null || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var previousProcess = Process.GetProcessById(processId.Value);
            previousProcess.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateEvent(
        nint eventAttributes,
        bool manualReset,
        bool initialState,
        string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(nint eventHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint flags,
        uint milliseconds,
        ulong handleCount,
        nint[] handles,
        out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
