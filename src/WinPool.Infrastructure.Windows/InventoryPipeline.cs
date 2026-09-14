using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsPowerShellRunner : IReadOnlyInventoryCommandRunner
{
    private readonly CollectionPurpose purpose;
    public WindowsPowerShellRunner(CollectionPurpose purpose = CollectionPurpose.Hardware) => this.purpose = purpose;
    public const int TimeoutSeconds = 60;
    public static string ExecutablePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    public async Task<ReadOnlyCommandResult> RunInventoryAsync(
        CancellationToken cancellationToken)
    {
        var command = EmbeddedStorageInventoryScript.ForPurpose(purpose);
        ReadOnlyStorageCommandPolicy.EnsureSafe(command);
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
            command.AsMemory(),
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
    private readonly IReadOnlyInventoryCommandRunner _runner;
    private readonly CollectionPurpose _purpose;

    public WindowsHardwareInventoryProvider(IReadOnlyInventoryCommandRunner? runner = null, CollectionPurpose purpose = CollectionPurpose.Storage)
    {
        _purpose = purpose;
        _runner = runner ?? new WindowsPowerShellRunner(purpose);
    }

    public static string FixedStorageCommand => EmbeddedStorageInventoryScript.Source;

    public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) =>
        new WindowsHardwareInventoryProvider(purpose: CollectionPurpose.Hardware).CollectLocalAsync(cancellationToken);

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
            using var parsed = JsonDocument.Parse(result.StandardOutput);
            var root = parsed.RootElement;
            var capturedAt = root.GetProperty("ScannedAt").GetDateTimeOffset();
            var name = root.GetProperty("Computer").GetProperty("Name").GetString() ?? Environment.MachineName;
            var context = StorageSnapshot.Empty(name) with { ScannedAt = capturedAt,
                SnapshotVersion = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(result.StandardOutput))) };
            var id = $"local:{context.Computer.StableId}";
            var system = InternalStableIdentity.SystemFromDocumentId(id);
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, id, StorageSystemKind.Local,
                name, WinPoolFactCapture.Read(root, context, system, _purpose), [], capturedAt);
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
