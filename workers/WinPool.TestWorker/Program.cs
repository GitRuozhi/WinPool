namespace WinPool.TestWorker;

internal static class Program
{
    private const int InvalidInvocationExitCode = 64;
    private const int RuntimeFailureExitCode = 70;

    public static async Task<int> Main(string[] args)
    {
        if (!TestWorkerLaunchOptions.TryParse(args, out var options))
        {
            Console.Error.WriteLine(
                "WinPool.TestWorker requires an Agent-issued private IPC endpoint.");
            return InvalidInvocationExitCode;
        }

        try
        {
            return await new TestWorkerPipeClient(options!)
                .RunAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or TimeoutException)
        {
            Console.Error.WriteLine($"WinPool.TestWorker IPC failure: {exception.Message}");
            return RuntimeFailureExitCode;
        }
    }
}
