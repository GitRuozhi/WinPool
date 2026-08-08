using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinPool.Ipc;

public static class CurrentUserPipeFactory
{
    private const PipeOptions ServerOptions =
        PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance;

    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        ValidatePipeName(pipeName);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new PipeAccessRule(
                user,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            ServerOptions,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    public static NamedPipeClientStream CreateClient(string pipeName)
    {
        ValidatePipeName(pipeName);
        return new NamedPipeClientStream(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public static int GetConnectedClientProcessId(NamedPipeServerStream server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsConnected)
        {
            throw new InvalidOperationException("The named pipe server is not connected.");
        }

        if (!GetNamedPipeClientProcessId(
                server.SafePipeHandle.DangerousGetHandle(),
                out var processId)
            || processId == 0
            || processId > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Could not resolve the named-pipe client process. Win32={Marshal.GetLastWin32Error()}.");
        }

        return checked((int)processId);
    }

    public static int GetConnectedServerProcessId(NamedPipeClientStream client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.IsConnected)
        {
            throw new InvalidOperationException("The named pipe client is not connected.");
        }

        if (!GetNamedPipeServerProcessId(
                client.SafePipeHandle.DangerousGetHandle(),
                out var processId)
            || processId == 0
            || processId > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Could not resolve the named-pipe server process. Win32={Marshal.GetLastWin32Error()}.");
        }

        return checked((int)processId);
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!pipeName.StartsWith("WinPool.", StringComparison.Ordinal)
            || pipeName.Contains('\\')
            || pipeName.Contains('/'))
        {
            throw new ArgumentException("Only generated local WinPool pipe names are allowed.", nameof(pipeName));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        nint pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        nint pipe,
        out uint serverProcessId);
}
