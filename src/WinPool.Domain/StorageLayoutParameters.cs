namespace WinPool.Domain;

/// <summary>Requested Windows layout values, independent of provider and member identities.</summary>
public sealed record StorageLayoutParameters(
    string ResiliencySettingName,
    int NumberOfColumns,
    int InterleaveBytes,
    int NumberOfDataCopies = 1,
    int PhysicalDiskRedundancy = 0)
{
    public static StorageLayoutParameters LegacyDefault { get; } = new("Simple", 1, 65536);

    public static StorageLayoutParameters FromLegacy(int columns, int interleave) =>
        new("Simple", columns, interleave);

    /// <summary>Checks serialized fields only; actual member and provider support is checked from fresh facts.</summary>
    public void Validate()
    {
        if (ResiliencySettingName is not ("Simple" or "Mirror" or "Parity") ||
            NumberOfColumns < 1 || NumberOfDataCopies < 1 || PhysicalDiskRedundancy < 0 ||
            InterleaveBytes is not (16384 or 32768 or 65536 or 131072 or 262144))
            throw new ArgumentException("The storage layout requires known resiliency, positive columns and copies, nonnegative redundancy, and a supported interleave.");
        if (ResiliencySettingName == "Simple" && (NumberOfDataCopies != 1 || PhysicalDiskRedundancy != 0)
            || ResiliencySettingName == "Mirror" && (NumberOfDataCopies is not (2 or 3) || PhysicalDiskRedundancy != NumberOfDataCopies - 1)
            || ResiliencySettingName == "Parity" && PhysicalDiskRedundancy is not (1 or 2))
            throw new ArgumentException("The copies and disk redundancy must describe the selected resiliency.");
    }

    /// <summary>Uses the shared capacity rules to check columns, copies and parity against actual data members.</summary>
    public ConservativeCapacityEstimate EstimateCapacity(IReadOnlyList<long> dataMemberBytes,
        long knownPhysicalAllocatedBytes = 0)
    {
        Validate();
        return ConservativeCapacity.PlanLogicalUpperBound(dataMemberBytes, ResiliencySettingName,
            NumberOfDataCopies, NumberOfColumns,
            ResiliencySettingName == "Parity" ? PhysicalDiskRedundancy : 0,
            InterleaveBytes, knownPhysicalAllocatedBytes);
    }
}
