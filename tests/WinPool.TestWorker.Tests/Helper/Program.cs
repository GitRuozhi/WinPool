using System.Diagnostics;
using System.Text.Json;

namespace WinPool.TestWorker.ProcessHelper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 64;
        }

        switch (args[0])
        {
            case "echo-args":
                Console.WriteLine(JsonSerializer.Serialize(args.Skip(1)));
                return 0;

            case "exit-code":
                return args.Length == 2
                       && int.TryParse(
                           args[1],
                           System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture,
                           out var exitCode)
                    ? exitCode
                    : 64;

            case "wait":
                Console.WriteLine("READY");
                await Console.Out.FlushAsync().ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;

            case "spawn-child":
                return await SpawnChildAsync(args).ConfigureAwait(false);

            case "child-wait":
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;

            default:
                return 64;
        }
    }

    private static async Task<int> SpawnChildAsync(string[] args)
    {
        if (args.Length != 2
            || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return 64;
        }

        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "child-wait" }
        });
        if (child is null)
        {
            return 70;
        }

        await File.WriteAllTextAsync(
                args[1],
                child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        Console.WriteLine("READY");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }
}
