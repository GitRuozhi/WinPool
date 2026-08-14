namespace WinPool.Application;

/// <summary>
/// Defines which locally projected operating-system disks can be displayed in
/// the partition workspace. Display eligibility is deliberately separate from
/// whether a particular operation is safe to enable.
/// </summary>
public static class PartitionableDiskPolicy
{
    public static bool IsEligible(OsDiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        return disk.Size > 0
            && (disk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                || disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                || disk.PartitionStyle.Equals("MBR", StringComparison.OrdinalIgnoreCase));
    }
}
