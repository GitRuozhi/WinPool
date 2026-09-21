using System.Diagnostics;
using System.Text;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class ControlledProcessRunnerTests
{
    [Fact]
    public async Task RunAsyncDrainsLargeDualChannelOutputWithinBoundedCapture()
    {
        await using var fixture = await CommandFixture.CreateAsync(
            "large-output.cmd",
            """
            @echo off
            for /L %%I in (1,1,12000) do (
              echo OUT-012345678901234567890123456789012345678901234567890123456789
              >&2 echo ERR-012345678901234567890123456789012345678901234567890123456789
            )
            """);
        var runner = new ControlledProcessRunner();

        var result = await runner.RunAsync(
            new ControlledProcessInvocation(
                CommandFixture.CommandProcessorPath,
                ["/d", "/c", fixture.ScriptPath]),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("OUT-", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ERR-", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[WinPool process output truncated]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[WinPool process output truncated]", result.StandardError, StringComparison.Ordinal);
        Assert.InRange(result.StandardOutput.Length, 1, 65_600);
        Assert.InRange(result.StandardError.Length, 1, 65_600);
        Assert.True(result.PeakWorkingSetBytes > 0);
        Assert.True(result.ProcessorTimeMilliseconds >= 0);
    }

    [Fact]
    public async Task CancellationTerminatesOwnedProcessTreeBeforeReturning()
    {
        await using var fixture = await CommandFixture.CreateAsync(
            "wait.cmd",
            """
            @echo off
            ping -n 31 127.0.0.1 > nul
            """);
        var runner = new ControlledProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                new ControlledProcessInvocation(
                    CommandFixture.CommandProcessorPath,
                    ["/d", "/c", fixture.ScriptPath]),
                cancellation.Token));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            "Cancellation did not wait for the owned child tree to exit promptly.");
    }

    [Fact]
    public async Task BinaryConsumerFailureTerminatesChildBeforePipeCanDeadlock()
    {
        await using var fixture = await CommandFixture.CreateAsync(
            "unbounded-output.cmd",
            """
            @echo off
            :again
            echo uninterrupted-output
            goto again
            """);
        var runner = new ControlledProcessRunner();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunBinaryOutputAsync(
                    new ControlledProcessInvocation(
                        CommandFixture.CommandProcessorPath,
                        ["/d", "/c", fixture.ScriptPath]),
                    async (stream, cancellationToken) =>
                    {
                        var buffer = new byte[128];
                        _ = await stream.ReadAsync(buffer, cancellationToken);
                        throw new InvalidOperationException("consumer failed intentionally");
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("consumer failed intentionally", exception.Message);
    }

    private sealed class CommandFixture : IAsyncDisposable
    {
        private CommandFixture(string directory, string scriptPath)
        {
            Directory = directory;
            ScriptPath = scriptPath;
        }

        public static string CommandProcessorPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");

        public string Directory { get; }

        public string ScriptPath { get; }

        public static async Task<CommandFixture> CreateAsync(
            string fileName,
            string contents)
        {
            Assert.True(File.Exists(CommandProcessorPath));
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"WinPool runner 空间 {Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var scriptPath = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(scriptPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new CommandFixture(directory, scriptPath);
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
