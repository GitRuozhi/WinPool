using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WinPool.Domain;

public readonly record struct SystemId(Guid Value)
{
    public static SystemId New() => new(Guid.NewGuid());
}

public readonly record struct EnvironmentId(Guid Value)
{
    public static EnvironmentId New() => new(Guid.NewGuid());
}

public readonly record struct OperationId(Guid Value)
{
    public static OperationId New() => new(Guid.NewGuid());
}

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
}

public readonly record struct StorageObjectId
{
    [JsonConstructor]
    public StorageObjectId(SystemId system, StorageObjectKind kind, string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        System = system;
        Kind = kind;
        ProviderKey = providerKey.Trim();
    }

    public SystemId System { get; }
    public StorageObjectKind Kind { get; }
    public string ProviderKey { get; }
}

public enum StorageObjectKind
{
    System,
    StorageSubsystem,
    StoragePool,
    StorageTier,
    PhysicalDisk,
    VirtualDisk,
    OsDisk,
    Partition,
    NetworkDisk,
    LogicalGroup
}

public enum ExecutionMode
{
    Simulation,
    Real
}

public enum PrivilegeState
{
    StandardUser,
    Administrator
}

public enum EnvironmentKind
{
    ProtectedDevelopmentMachine,
    Simulation,
    Replay,
    UserProvidedDisposableMachine,
    RemoteAgent
}

public enum AlgorithmConfidence
{
    Proven,
    Derived,
    Speculative
}

public sealed record AlgorithmIdentity(
    string Id,
    string Version,
    AlgorithmConfidence Confidence,
    string EvidenceReference)
{
    public bool RequiresUnverifiedLabel => Confidence == AlgorithmConfidence.Speculative;
}

public static class MachineBinding
{
    public static string Create(IEnumerable<string> stableMachineValues)
    {
        ArgumentNullException.ThrowIfNull(stableMachineValues);
        var normalized = stableMachineValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one stable machine value is required.", nameof(stableMachineValues));
        }

        var material = string.Join('\n', normalized);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
