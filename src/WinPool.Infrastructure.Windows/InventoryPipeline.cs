using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinPool.Core;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsPowerShellRunner : IReadOnlyInventoryCommandRunner
{
    public const int TimeoutSeconds = 60;
    public static string ExecutablePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    public async Task<ReadOnlyCommandResult> RunInventoryAsync(
        CancellationToken cancellationToken)
    {
        ReadOnlyStorageCommandPolicy.EnsureSafe(EmbeddedStorageInventoryScript.Source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(
            "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); "
            + "& ([ScriptBlock]::Create([Console]::In.ReadToEnd()))");

        var startedAt = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Windows PowerShell 5.1.");
        }

        await process.StandardInput.WriteAsync(
            EmbeddedStorageInventoryScript.Source.AsMemory(),
            timeout.Token);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Read-only inventory exceeded {TimeoutSeconds} seconds.");
            }
            throw;
        }

        return new ReadOnlyCommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask,
            Stopwatch.GetElapsedTime(startedAt));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public static partial class ReadOnlyStorageCommandPolicy
{
    [GeneratedRegex(
        @"(?im)(?:^|[|;]\s*)(?:New|Set|Remove|Clear|Initialize|Format|Resize|Repair|Optimize|Reset|Update)-(?:Disk|Partition|Volume|Storage|PhysicalDisk|VirtualDisk|StorageTier)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex MutatingStorageCommandRegex();

    public static void EnsureSafe(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("A fixed read-only command is required.");
        }
        if (MutatingStorageCommandRegex().IsMatch(command))
        {
            throw new InvalidOperationException("A mutating storage command was rejected.");
        }
    }
}

public sealed class WindowsHardwareInventoryProvider : IHardwareInventoryProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly IReadOnlyInventoryCommandRunner _runner;

    public WindowsHardwareInventoryProvider(IReadOnlyInventoryCommandRunner? runner = null)
    {
        _runner = runner ?? new WindowsPowerShellRunner();
    }

    public static string FixedStorageCommand => EmbeddedStorageInventoryScript.Source;

    public async Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunInventoryAsync(cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InventoryScanException(
                $"The read-only inventory process failed with exit code {result.ExitCode}.",
                result.StandardError);
        }
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InventoryScanException(
                "The read-only inventory process returned no JSON.",
                result.StandardError);
        }

        try
        {
            var raw = JsonSerializer.Deserialize<RawSnapshot>(result.StandardOutput, JsonOptions)
                ?? throw new JsonException("The JSON snapshot was empty.");
            var snapshot = RawSnapshotProjector.Project(raw, result.StandardOutput);
            var report = HardwareReportFactory.Create(
                snapshot,
                raw,
                result.StandardError,
                result.Duration);
            return new StorageSystemDocument(
                StorageSystemDocument.CurrentSchemaVersion,
                $"local:{snapshot.Computer.StableId}",
                StorageSystemKind.Local,
                snapshot.Computer.Name,
                snapshot,
                report,
                [],
                snapshot.ScannedAt);
        }
        catch (JsonException ex)
        {
            throw new InventoryScanException(
                $"The inventory JSON could not be parsed: {ex.Message}",
                result.StandardError,
                ex);
        }
    }
}

public sealed class WindowsStorageInventoryProvider : IStorageInventoryProvider
{
    private readonly IHardwareInventoryProvider _provider;

    public WindowsStorageInventoryProvider(IHardwareInventoryProvider? provider = null)
    {
        _provider = provider ?? new WindowsHardwareInventoryProvider();
    }

    public async Task<StorageSnapshot> ScanAsync(CancellationToken cancellationToken) =>
        (await _provider.CollectLocalAsync(cancellationToken)).Snapshot;
}
