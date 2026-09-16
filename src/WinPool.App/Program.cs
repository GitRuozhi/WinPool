using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinPool.Application;
using WinPool.Infrastructure.Windows;
using WinPool.Ipc;

namespace WinPool_App;

public static class Program
{
    private const string SingleInstanceKey = "WinPool.SingleInstance";
    private static nint s_redirectEventHandle;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!WaitForElevationHandoff(args, out var isElevationHandoff))
        {
            return 2;
        }

        if (!isElevationHandoff)
        {
            WaitForProcessHandoff(args);
        }
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

    /// <summary>
    /// An elevated replacement stays outside WinUI, the single-instance key and
    /// Agent startup until the exact old App and Agent instances have exited.
    /// This prevents an elevated UI from silently reconnecting to the previous
    /// medium-integrity Agent.
    /// </summary>
    private static bool WaitForElevationHandoff(
        IReadOnlyList<string> arguments,
        out bool isElevationHandoff)
    {
        isElevationHandoff = arguments.Any(argument =>
            argument.Equals(
                ApplicationStartupOptions.ElevatedRealArgument,
                StringComparison.OrdinalIgnoreCase));
        if (!isElevationHandoff)
        {
            return true;
        }

        if (!ApplicationStartupOptions.TryGetElevationHandoff(arguments, out var handoff))
        {
            WriteElevationHandoffDiagnostic("elevation.handoff.arguments_invalid", null);
            ReportElevationHandoffFailure("启动参数不完整 / The elevation handoff arguments are incomplete.");
            return false;
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)
            || !IsExpectedHandoffEvent(handoff.ReadyEventName, "Ready", sid)
            || !IsExpectedHandoffEvent(handoff.ContinueEventName, "Continue", sid))
        {
            WriteElevationHandoffDiagnostic("elevation.handoff.signal_invalid", null);
            ReportElevationHandoffFailure("交接信号身份无效 / The elevation handoff signals are invalid.");
            return false;
        }

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(handoff.ReadyEventName);
            using var continueEvent = EventWaitHandle.OpenExisting(handoff.ContinueEventName);
            readyEvent.Set();
            if (!continueEvent.WaitOne(TimeSpan.FromSeconds(30)))
            {
                // The old App remains responsible for explaining why it did
                // not begin shutdown. Do not open a business window here.
                WriteElevationHandoffDiagnostic("elevation.handoff.continuation_timeout", null);
                return false;
            }
        }
        catch (WaitHandleCannotBeOpenedException exception)
        {
            WriteElevationHandoffDiagnostic("elevation.handoff.signal_unavailable", exception);
            ReportElevationHandoffFailure("交接信号已失效 / The elevation handoff signals are unavailable.");
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            WriteElevationHandoffDiagnostic("elevation.handoff.signal_access_denied", exception);
            ReportElevationHandoffFailure("交接信号访问被拒绝 / Access to the elevation handoff signals was denied.");
            return false;
        }

        var appPath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("The WinPool executable path is unavailable."));
        var agentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "WinPool.Agent.exe"));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var app = ObserveHandoffProcess(handoff.AppProcess, appPath);
            var agent = ObserveHandoffProcess(handoff.AgentProcess, agentPath);
            if (app == HandoffProcessState.Exited && agent == HandoffProcessState.Exited)
            {
                return true;
            }

            if (app is HandoffProcessState.IdentityMismatch or HandoffProcessState.Unreadable
                || agent is HandoffProcessState.IdentityMismatch or HandoffProcessState.Unreadable)
            {
                WriteElevationHandoffDiagnostic(
                    $"elevation.handoff.identity_invalid.app-{app}.agent-{agent}",
                    null);
                ReportElevationHandoffFailure(
                    $"旧进程身份无法核验（App: {app}; Agent: {agent}）/ "
                    + $"The old process identity could not be verified (App: {app}; Agent: {agent}).");
                return false;
            }

            Thread.Sleep(100);
        }

        var finalApp = ObserveHandoffProcess(handoff.AppProcess, appPath);
        var finalAgent = ObserveHandoffProcess(handoff.AgentProcess, agentPath);
        WriteElevationHandoffDiagnostic(
            $"elevation.handoff.exit_timeout.app-{finalApp}.agent-{finalAgent}",
            null);
        ReportElevationHandoffFailure(
            $"旧进程未在时限内退出（App: {finalApp}; Agent: {finalAgent}）。请重新打开 WinPool。\r\n"
            + $"The old processes did not exit in time (App: {finalApp}; Agent: {finalAgent}). Please reopen WinPool.");
        return false;
    }

    private static bool IsExpectedHandoffEvent(string name, string stage, string sid)
    {
        var prefix = $"Local\\WinPool.Elevation.{stage}.{IpcIdentity.HashUserSid(sid)[..24]}.";
        return name.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static HandoffProcessState ObserveHandoffProcess(
        ProcessHandoffWitness expected,
        string expectedPath)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            if (process.HasExited)
            {
                return HandoffProcessState.Exited;
            }

            var startedAtUtc = process.StartTime.ToUniversalTime();
            if (Math.Abs((startedAtUtc - expected.StartedAtUtc.UtcDateTime).TotalSeconds) > 5)
            {
                return HandoffProcessState.IdentityMismatch;
            }

            var imagePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(imagePath)
                   && string.Equals(
                       Path.GetFullPath(imagePath),
                       expectedPath,
                       StringComparison.OrdinalIgnoreCase)
                ? HandoffProcessState.Running
                : HandoffProcessState.IdentityMismatch;
        }
        catch (ArgumentException)
        {
            return HandoffProcessState.Exited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return HandoffProcessState.Unreadable;
        }
    }

    private static void ReportElevationHandoffFailure(string details) =>
        MessageBox(
            nint.Zero,
            $"WinPool 管理员重启未完成。新实例没有启动。\r\n\r\n{details}",
            "WinPool",
            0x00000010);

    private static void WriteElevationHandoffDiagnostic(string source, Exception? exception)
    {
        try
        {
            DiagnosticLog.AppendFailure(
                StorageDataLocations.ResolveCurrentRoot(AppContext.BaseDirectory),
                "elevation-handoff.jsonl",
                source,
                exception);
        }
        catch
        {
        }
    }

    private enum HandoffProcessState
    {
        Exited,
        Running,
        IdentityMismatch,
        Unreadable
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);

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
