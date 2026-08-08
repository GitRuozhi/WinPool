using System.Diagnostics;
using WinPool.Application;

namespace WinPool.TestWorker.Tests;

internal static class TestWorkerTestSupport
{
    public static string HelperPath
    {
        get
        {
            var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = baseDirectory.Parent?.Name
                ?? throw new InvalidOperationException("Could not resolve build configuration.");
            var path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "Helper",
                "bin",
                configuration,
                "net10.0",
                "WinPool.TestWorker.ProcessHelper.exe"));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The controlled process helper was not built.", path);
            }

            return path;
        }
    }

    public static ToolProcessRequest CreateRequest(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        TimeSpan? gracefulTimeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var path = HelperPath;
        var toolId = new ToolId("winpool.controlled-test-helper");
        var invocation = new ToolInvocation(
            toolId,
            path,
            arguments,
            Path.GetDirectoryName(path)!,
            environment ?? new Dictionary<string, string>(),
            ToolOutputEncoding.Utf8,
            timeout);
        var state = new ToolState(
            toolId,
            ToolAvailability.Available,
            path,
            ToolPathSource.CustomPath,
            null,
            null,
            null,
            ToolCapabilities.StructuredOutput,
            false);
        return new ToolProcessRequest(
            TestRunId.New(),
            "controlled-step",
            invocation,
            state,
            gracefulTimeout ?? TimeSpan.FromMilliseconds(100));
    }

    public static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 0)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // The helper may have created the file but not released it yet.
                }
            }

            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for helper evidence at '{path}'.");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    public static async Task<int> WaitForInt32FileAsync(
        string path,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                if (int.TryParse(
                        text,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value) &&
                    value > 0)
                {
                    return value;
                }
            }
            catch (IOException)
            {
                // Creation and release of this short evidence file are asynchronous.
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Timed out waiting for integer helper evidence at '{path}'.");
    }

    public static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
