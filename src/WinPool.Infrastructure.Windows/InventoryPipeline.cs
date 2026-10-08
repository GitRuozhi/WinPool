using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsPowerShellRunner : IReadOnlyInventoryCommandRunner, IScopedInventoryCommandRunner
{
    private readonly CollectionPurpose purpose;
    public WindowsPowerShellRunner(CollectionPurpose purpose = CollectionPurpose.Hardware) => this.purpose = purpose;
    public const int TimeoutSeconds = 60;
    public static string ExecutablePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    public Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(EmbeddedStorageInventoryScript.ForPurpose(purpose), cancellationToken);

    public Task<ReadOnlyCommandResult> RunInventoryAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
        RunCommandAsync(ScopedStorageInventoryScript.Create(scope), cancellationToken);

    private static async Task<ReadOnlyCommandResult> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
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

public sealed class WindowsHardwareInventoryProvider : IHardwareInventoryProvider, IScopedHardwareInventoryProvider
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

    public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) =>
        CollectAsync(null, cancellationToken);

    public Task<StorageSystemDocument> CollectScopedAsync(StorageInventoryScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        return CollectAsync(scope, cancellationToken);
    }

    private async Task<StorageSystemDocument> CollectAsync(StorageInventoryScope? scope, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var captureStarted = Stopwatch.GetTimestamp();
        var result = scope is null ? await _runner.RunInventoryAsync(cancellationToken)
            : _runner is IScopedInventoryCommandRunner scoped ? await scoped.RunInventoryAsync(scope, cancellationToken)
            : throw new InvalidOperationException("The inventory runner does not support exact scoped collection.");
        StorageOperationTiming.Record("inventory.provider.collect", captureStarted, scope?.OperationId, scope?.StepId, scope?.Key ?? _purpose.ToString(), 1);
        Trace.WriteLine(JsonSerializer.Serialize(new { Event = "inventory.capture", Purpose = scope is null ? _purpose.ToString() : "ScopedStorage",
            OperationId = scope?.OperationId.Value, StepId = scope?.StepId, Scope = scope?.Key, Generation = scope?.Generation,
            ElapsedMilliseconds = result.Duration.TotalMilliseconds, result.ExitCode, StartedAt = started }));
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
            if (scope is not null && !StringComparer.OrdinalIgnoreCase.Equals(name, Environment.MachineName))
                throw new InvalidDataException("Scoped inventory returned a foreign machine.");
            var context = StorageSnapshot.Empty(name) with { ScannedAt = capturedAt,
                SnapshotVersion = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(result.StandardOutput))) };
            var id = $"local:{context.Computer.StableId}";
            var system = scope?.SystemId ?? InternalStableIdentity.SystemFromDocumentId(id);
            var facts = WinPoolFactCapture.Read(root, context, system, scope is null ? _purpose : CollectionPurpose.Storage);
            if (scope is not null)
            {
                string[] required = ["MSFT_StorageSubSystem", "MSFT_StoragePool", "MSFT_PhysicalDisk", "MSFT_VirtualDisk",
                    "MSFT_StorageTier", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume"];
                var complete = required.All(name => facts.Sources.Any(x => x.ClassName == name && x.ReadState == FieldReadState.Returned))
                    && facts.Sources.All(x => x.ReadState is FieldReadState.Returned or FieldReadState.Unavailable)
                    && facts.Sources.Where(x => x.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)).All(x => x.ReadState == FieldReadState.Returned);
                var coveredIds = scope.BeforeObjectIds.Concat(facts.Objects.Select(x => x.Id)).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToImmutableArray();
                facts = facts with
                {
                    Sources = facts.Sources.Select(x => x.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                        || x.ClassName is "Windows.DiskRoles" or "Win32_DiskDrive"
                        ? x with { Coverage = new(scope.Key, coveredIds, complete && x.ReadState == FieldReadState.Returned, scope.Generation) }
                        : x).ToImmutableArray(),
                    Collections = [],
                    ScopedCollection = new(scope, capturedAt, DateTimeOffset.UtcNow, complete,
                        complete ? null : "inventory.scope.incomplete")
                };
            }
            if (scope is null && _purpose == CollectionPurpose.Hardware)
            {
                facts = WindowsGraphicsFactCollector.AddTo(facts, capturedAt);
                facts = WindowsNetworkFactCollector.AddTo(facts, capturedAt);
            }
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, id, StorageSystemKind.Local,
                name, facts, [], capturedAt) { SystemId = system };
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
