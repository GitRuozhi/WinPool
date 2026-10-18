using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

[CollectionDefinition("PowerShellHost", DisableParallelization = true)]
public sealed class PowerShellHostCollection { }

[Collection("PowerShellHost")]
public sealed class WindowsPowerShellHostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly PowerShellHostTemplates Templates = new(
        """
        param($p)
        if ($null -ne (Get-Variable requestLocal -Scope Local -ErrorAction SilentlyContinue)) { throw 'request-local-leaked' }
        $requestLocal=1
        [Console]::Error.WriteLine(('fixture-inventory:' + [string]$p.Value))
        if($p.DelayMilliseconds) { Start-Sleep -Milliseconds ([int]$p.DelayMilliseconds) }
        [Console]::Error.Write(('diagnostic-'*3000))
        @{ ProcessId=$PID; Value=[string]$p.Value } | ConvertTo-Json -Compress
        """,
        """
        param($p)
        if($p.DelayMilliseconds) { Start-Sleep -Milliseconds ([int]$p.DelayMilliseconds) }
        if($p.ExitWithoutReply) { [Environment]::Exit(0) }
        @{ ProcessId=$PID; Value=[string]$p.Value } | ConvertTo-Json -Compress
        """, "param($kind,$p); throw 'fixture-query-not-used'", false);

    [Fact]
    public async Task ConsecutiveReadWriteReadReusesExactHostAndRequestScopes()
    {
        await using var host = new WindowsPowerShellHost(Templates);
        var first = await ReadAsync(host, "first");
        await using var lease = await host.ReserveWriteAsync(default);
        using var written = JsonDocument.Parse(await lease.ExecuteAsync(new { Value = "write", DelayMilliseconds = 0, ExitWithoutReply = false }, default));
        var second = await ReadAsync(host, "second");
        Assert.Equal(first, second);
        Assert.Equal(first, lease.HostCall.ProcessId);
        Assert.Equal(first, written.RootElement.GetProperty("ProcessId").GetInt32());
        Assert.True(lease.RequestCompleted);
        Assert.True(await host.PrepareShutdownAsync(default), host.ShutdownBlockedReason);
        output.WriteLine(PowerShellHostProcess.DescribeGlobalJob());
    }

    [Fact]
    public async Task ReadDeadlineReturnsThenOldHostExitsBeforeExplicitReplacement()
    {
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var originalSink = StorageOperationTiming.Sink;
        StorageOperationTiming.Sink = diagnostics.Enqueue;
        try
        {
            await using var host = new WindowsPowerShellHost(Templates);
            var first = await ReadAsync(host, "warm");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.QueryAsync(WindowsPowerShellQuery.Inventory,
                new { Value = "timed-out", DelayMilliseconds = 5000 }, deadline.Token));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
            var replacement = await ReadAsync(host, "replacement");
            Assert.NotEqual(first, replacement);
            Assert.True(Exited(first));
            var failures = diagnostics.Select(value => JsonDocument.Parse(value)).ToArray();
            try
            {
                var failure = Assert.Single(failures, value => value.RootElement.GetProperty("phase").GetString() == "host.read.failure").RootElement;
                Assert.Equal("Inventory", failure.GetProperty("scope").GetString());
                Assert.Equal(first, failure.GetProperty("hostProcessId").GetInt32());
                Assert.NotEqual(Guid.Empty, failure.GetProperty("correlationId").GetGuid());
                Assert.NotEqual(Guid.Empty, failure.GetProperty("hostIncarnation").GetGuid());
                Assert.True(failure.GetProperty("requestStarted").GetBoolean());
                var tail = failure.GetProperty("recentStderrTail").GetString()!;
                Assert.Contains("fixture-inventory:timed-out", tail);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(tail) <= 16 * 1024);
            }
            finally { foreach (var failure in failures) failure.Dispose(); }
        }
        finally { StorageOperationTiming.Sink = originalSink; }
    }

    [Fact]
    public async Task QueuedReadCancellationAndCallerCancellationDoNotKillIssuedWrite()
    {
        await using var host = new WindowsPowerShellHost(Templates);
        var processId = await ReadAsync(host, "warm");
        await using var lease = await host.ReserveWriteAsync(default);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var write = lease.ExecuteAsync(new { Value = "write", DelayMilliseconds = 450, ExitWithoutReply = false }, caller.Token);
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.QueryAsync(WindowsPowerShellQuery.Inventory,
            new { Value = "queued", DelayMilliseconds = 0 }, readDeadline.Token));
        Assert.False(host.CanBeginShutdown);
        Assert.False(Exited(processId));
        using var result = JsonDocument.Parse(await write);
        Assert.Equal(processId, result.RootElement.GetProperty("ProcessId").GetInt32());
        Assert.Equal(processId, await ReadAsync(host, "after-write"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ExecuteAsync(new { }, default));
    }

    [Fact]
    public async Task SecondHostCannotBeCreatedWhileFirstHasAnyLiveMember()
    {
        await using var first = new WindowsPowerShellHost(Templates);
        var firstProcess = await ReadAsync(first, "first");
        await using var second = new WindowsPowerShellHost(Templates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(second, "second"));
        Assert.False(await second.PrepareShutdownAsync(default));
        Assert.Equal(firstProcess, await ReadAsync(first, "still-first"));
    }

    [Fact]
    public async Task LostWriteReplyIsNotReplayedAndNewReadWaitsForOldExit()
    {
        await using var host = new WindowsPowerShellHost(Templates);
        var first = await ReadAsync(host, "warm");
        await using var lease = await host.ReserveWriteAsync(default);
        await Assert.ThrowsAsync<IOException>(() => lease.ExecuteAsync(new { Value = "write", DelayMilliseconds = 0, ExitWithoutReply = true }, default));
        Assert.False(lease.RequestCompleted);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.WaitForCurrentExitSignalAsync(deadline.Token);
        var next = await ReadAsync(host, "read-only-after-exit");
        Assert.True(Exited(first)); Assert.NotEqual(first, next);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("malformed")]
    [InlineData("throw")]
    public async Task AdapterPersistsExactCompletionEvenWhenTheEndedFixedCommandResultIsUnknown(string reply)
    {
        var templates = Templates with { Write = """
            param($p)
            if($p.Command.Name -eq 'throw') { throw 'fixed-command-failed' }
            if($p.Command.Name -eq 'malformed') { return 'invalid-provider-json' }
            @{ProviderReturned=$true;Code='provider.returned'} | ConvertTo-Json -Compress
            """ };
        await using var host = new WindowsPowerShellHost(templates);
        var adapter = new WindowsRealStorageCommandAdapter(host);
        await using var lease = await host.ReserveWriteAsync(default);
        var target = new WindowsStorageCommandTarget(StorageObjectKind.PhysicalDisk, "physical-unique", "physical-object",
            "serial", "", "", null, null, "", null, null, "", "physical-unique", "subsystem", "fresh")
            { HostCall = lease.HostCall };
        var command = new CreatePoolCommand(RealTargetReference.ForExisting(
            new StorageObjectId(SystemId.New(), StorageObjectKind.PhysicalDisk, "physical")), reply);
        var result = await adapter.ExecuteAsync(command, target, lease, default);
        Assert.Equal(lease.HostCall, result.CompletedHostCall);
        Assert.Equal(reply == "valid" ? "provider.returned" : reply == "throw"
            ? "adapter.process-outcome-unknown" : "adapter.response-outcome-unknown", result.Code);
    }

    [Fact]
    public async Task QueueHasEightWaitingSlotsAndCancellationReleasesAdmission()
    {
        await using var host = new WindowsPowerShellHost(Templates);
        await ReadAsync(host, "warm");
        await using var reserved = await host.ReserveWriteAsync(default);
        using var pending = new CancellationTokenSource();
        var queue = Enumerable.Range(0, 8).Select(index => host.QueryAsync(WindowsPowerShellQuery.Inventory,
            new { Value = index.ToString(), DelayMilliseconds = 0 }, pending.Token)).ToArray();
        Assert.Throws<InvalidOperationException>(() => { _ = host.QueryAsync(WindowsPowerShellQuery.Inventory,
            new { Value = "overflow", DelayMilliseconds = 0 }, default); });
        pending.Cancel();
        foreach (var item in queue) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => item);
        var following = ReadAsync(host, "admitted-after-cancel");
        await reserved.DisposeAsync();
        await following;
    }

    [Fact]
    public async Task ParentDeathRetainsGlobalSlotUntilExactOldChildHasExited()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = typeof(WindowsPowerShellHostTests).Assembly.Location.Split(Path.DirectorySeparatorChar)
            .First(part => part is "Release" or "Debug");
        var fixtureDirectory = Path.Combine(directory.FullName, "artifacts", "build", "WinPool.PowerShellHost.Fixture", configuration);
        var fixture = Assert.Single(Directory.GetFiles(fixtureDirectory, "WinPool.PowerShellHost.Fixture.dll", SearchOption.AllDirectories));
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(fixture);
        using var parent = Process.Start(start)!;
        var errors = parent.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var line = await parent.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(line is not null, parent.HasExited ? await errors : "The fixture did not publish its exact host identity.");
            Assert.StartsWith("HOSTCALL:", line);
            var call = JsonSerializer.Deserialize<PowerShellHostCall>(line!["HOSTCALL:".Length..])!;
            using var oldChild = Process.GetProcessById(call.ProcessId);
            Assert.Equal(call.StartedAtUtc.ToUnixTimeMilliseconds(), new DateTimeOffset(oldChild.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds());
            parent.Kill(); await parent.WaitForExitAsync(timeout.Token);
            Assert.False(PowerShellHostProcess.IsProcessExited(call.ProcessId));

            await using var replacement = new WindowsPowerShellHost(Templates);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(replacement, "must-not-start"));
            Assert.False(await replacement.PrepareShutdownAsync(timeout.Token));
            while (!await replacement.PrepareShutdownAsync(timeout.Token)) await Task.Delay(25, timeout.Token);
            Assert.True(PowerShellHostProcess.IsProcessExited(call.ProcessId));
            Assert.NotEqual(call.ProcessId, await ReadAsync(replacement, "new-read-only-host"));
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
        }
    }

    private static async Task<int> ReadAsync(WindowsPowerShellHost host, string value)
    {
        using var result = JsonDocument.Parse(await host.QueryAsync(WindowsPowerShellQuery.Inventory,
            new { Value = value, DelayMilliseconds = 0 }, default));
        Assert.Equal(value, result.RootElement.GetProperty("Value").GetString());
        return result.RootElement.GetProperty("ProcessId").GetInt32();
    }
    private static bool Exited(int processId)
    {
        return PowerShellHostProcess.IsProcessExited(processId);
    }
}
