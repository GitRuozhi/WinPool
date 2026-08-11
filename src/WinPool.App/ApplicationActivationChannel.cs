using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WinPool.Application;

namespace WinPool_App;

/// <summary>
/// Carries page requests from a redirected second launch to the existing
/// unpackaged WinUI process. Windows App SDK does not preserve raw command-line
/// arguments in AppActivationArguments for this handoff.
/// </summary>
internal static class ApplicationActivationChannel
{
    private static readonly Encoding WireEncoding = new UTF8Encoding(false);

    public static string PipeName => $"WinPool.Activation.{UserKey()}";

    public static bool TrySend(ApplicationStartupTarget target)
    {
        if (target == ApplicationStartupTarget.None)
        {
            return false;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.Out,
            PipeOptions.WriteThrough);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                pipe.Connect(100);
                break;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        if (!pipe.IsConnected)
        {
            return false;
        }

        using var writer = new StreamWriter(pipe, WireEncoding, 256, leaveOpen: true)
        {
            AutoFlush = true
        };
        writer.WriteLine(target.ToString());
        return true;
    }

    public static async Task ListenAsync(
        CancellationToken cancellationToken,
        Func<ApplicationStartupTarget, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, WireEncoding, false, 256, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (Enum.TryParse<ApplicationStartupTarget>(line, true, out var target)
                    && target != ApplicationStartupTarget.None)
                {
                    await handler(target).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string UserKey()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
    }
}
