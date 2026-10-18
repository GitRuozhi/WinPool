using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsPowerShellRunner : IReadOnlyInventoryCommandRunner, IScopedInventoryCommandRunner
{
    private readonly CollectionPurpose purpose;
    private readonly bool discoverProviderCache;
    private readonly WindowsPowerShellHost host;
    public WindowsPowerShellRunner(CollectionPurpose purpose = CollectionPurpose.Hardware, bool discoverProviderCache = false,
        WindowsPowerShellHost? host = null)
    { this.purpose = purpose; this.discoverProviderCache = discoverProviderCache; this.host = host ?? WindowsPowerShellHost.Shared; }
    public const int TimeoutSeconds = 60;
    public static string ExecutablePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");
    public Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken) => RunAsync(null, cancellationToken);
    public Task<ReadOnlyCommandResult> RunInventoryAsync(StorageInventoryScope scope, CancellationToken cancellationToken)
    { scope.Validate(); return RunAsync(scope, cancellationToken); }
    private async Task<ReadOnlyCommandResult> RunAsync(StorageInventoryScope? scope, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await host.QueryAsync(WindowsPowerShellQuery.Inventory,
            new { IncludeHardware = purpose == CollectionPurpose.Hardware, DiscoverProviderCache = discoverProviderCache,
                ScopeLocators = scope?.BeforeLocators, ComponentLocators = scope?.BeforeComponentLocators,
                EvidenceFields = WindowsRealStorageReadProfile.ForId(scope?.EvidenceProfile)?.Fields }, token).ConfigureAwait(false);
        return new(0, result, "", Stopwatch.GetElapsedTime(started));
    }
}
public sealed class WindowsHardwareInventoryProvider : IHardwareInventoryProvider, IScopedHardwareInventoryProvider
{
    private readonly IReadOnlyInventoryCommandRunner _runner;
    private readonly IReadOnlyInventoryCommandRunner _hardwareRunner;
    private readonly CollectionPurpose _purpose;

    public WindowsHardwareInventoryProvider(IReadOnlyInventoryCommandRunner? runner = null, CollectionPurpose purpose = CollectionPurpose.Storage,
        bool discoverProviderCache = false)
    {
        _purpose = purpose;
        _runner = runner ?? new WindowsPowerShellRunner(purpose, discoverProviderCache);
        _hardwareRunner = runner ?? new WindowsPowerShellRunner(CollectionPurpose.Hardware, discoverProviderCache);
    }

    public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) =>
        CollectAsync(CollectionPurpose.Hardware, null, cancellationToken);

    public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) =>
        CollectAsync(_purpose, null, cancellationToken);

    public Task<StorageSystemDocument> CollectScopedAsync(StorageInventoryScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        return CollectAsync(CollectionPurpose.Storage, scope, cancellationToken);
    }

    private async Task<StorageSystemDocument> CollectAsync(CollectionPurpose purpose, StorageInventoryScope? scope, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var captureStarted = Stopwatch.GetTimestamp();
        var runner = purpose == CollectionPurpose.Hardware ? _hardwareRunner : _runner;
        var result = scope is null ? await runner.RunInventoryAsync(cancellationToken)
            : _runner is IScopedInventoryCommandRunner scoped ? await scoped.RunInventoryAsync(scope, cancellationToken)
            : throw new InvalidOperationException("The inventory runner does not support exact scoped collection.");
        StorageOperationTiming.Record("inventory.provider.collect", captureStarted, scope?.OperationId, scope?.StepId, scope?.Key ?? purpose.ToString(), 1);
        Trace.WriteLine(JsonSerializer.Serialize(new { Event = "inventory.capture", Purpose = scope is null ? purpose.ToString() : "ScopedStorage",
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
            var facts = WinPoolFactCapture.Read(root, context, system, purpose);
            if (scope is not null)
            {
                var profile = WindowsRealStorageReadProfile.ForId(scope.EvidenceProfile);
                string[] required = profile is null ? ["MSFT_StorageSubSystem", "MSFT_StoragePool", "MSFT_PhysicalDisk", "MSFT_VirtualDisk",
                    "MSFT_StorageTier", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume"]
                    : root.TryGetProperty("ScopeQueriedClasses", out var queried) ? queried.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
                var complete = required.Length > 0 && required.All(name => facts.Sources.Any(x => x.ClassName == name && x.ReadState == FieldReadState.Returned))
                    && (profile is not null || facts.Sources.All(x => x.ReadState is FieldReadState.Returned or FieldReadState.Unavailable))
                    && (profile is null || root.TryGetProperty("ScopeClosureComplete", out var closureComplete) && closureComplete.ValueKind == JsonValueKind.True)
                    && facts.Sources.Where(x => x.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)).All(x => x.ReadState == FieldReadState.Returned);
                var coveredIds = scope.BeforeObjectIds.Concat(facts.Objects.Select(x => x.Id)).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToImmutableArray();
                facts = facts with
                {
                    Sources = facts.Sources.Select(x => x.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                        || profile?.Fields.ContainsKey(x.ClassName) == true
                        || x.ClassName is "Windows.DiskRoles" or "Win32_DiskDrive"
                        ? x with { Coverage = new(scope.Key, coveredIds, complete && x.ReadState == FieldReadState.Returned, scope.Generation)
                            { FieldNames = profile is not null && profile.Fields.TryGetValue(x.ClassName, out var fields) ? fields.ToImmutableArray() : null } }
                        : x).ToImmutableArray(),
                    Collections = [],
                    ScopedCollection = new(scope, capturedAt, DateTimeOffset.UtcNow, complete,
                        complete ? null : "inventory.scope.incomplete")
                };
            }
            if (scope is null && purpose == CollectionPurpose.Hardware)
            {
                facts = WindowsGraphicsFactCollector.AddTo(facts, capturedAt);
                facts = WindowsNetworkFactCollector.AddTo(facts, capturedAt,
                    root.TryGetProperty("NetworkSupplement", out var network) ? network : default);
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
