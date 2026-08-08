using System.Runtime.InteropServices;

namespace WinPool.Infrastructure.Windows;

public sealed record StorageSpacesVirtualDiskSample(
    string InstanceName,
    double ActiveBytes,
    double MissingBytes,
    double StaleBytes,
    double NeedRegenerationBytes,
    double RegeneratingBytes,
    double PendingDeletionBytes);

/// <summary>
/// Reads the inbox "Storage Spaces Virtual Disk" PDH counter set. These are
/// repair/health-state byte counters, not ordinary logical-volume throughput.
/// </summary>
public sealed class StorageSpacesVirtualDiskSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoScale = 0x00001000;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    private nint _query;
    private nint _active;
    private nint _missing;
    private nint _stale;
    private nint _needRegeneration;
    private nint _regenerating;
    private nint _pendingDeletion;

    public StorageSpacesVirtualDiskSampler()
    {
        if (PdhOpenQuery(null, nint.Zero, out _query) != 0)
        {
            _query = nint.Zero;
            return;
        }

        Add(@"\Storage Spaces Virtual Disk(*)\Virtual Disk Active Bytes", out _active);
        Add(@"\Storage Spaces Virtual Disk(*)\Virtual Disk Missing Bytes", out _missing);
        Add(@"\Storage Spaces Virtual Disk(*)\Virtual Disk Stale Bytes", out _stale);
        Add(
            @"\Storage Spaces Virtual Disk(*)\Virtual Disk Need Regeneration Bytes",
            out _needRegeneration);
        Add(
            @"\Storage Spaces Virtual Disk(*)\Virtual Disk Regenerating Bytes",
            out _regenerating);
        Add(
            @"\Storage Spaces Virtual Disk(*)\Virtual Disk Pending Deletion Bytes",
            out _pendingDeletion);
        PdhCollectQueryData(_query);
    }

    public IReadOnlyList<StorageSpacesVirtualDiskSample> Sample()
    {
        if (_query == nint.Zero || PdhCollectQueryData(_query) != 0)
        {
            return [];
        }

        var active = ReadCounter(_active);
        var missing = ReadCounter(_missing);
        var stale = ReadCounter(_stale);
        var needRegeneration = ReadCounter(_needRegeneration);
        var regenerating = ReadCounter(_regenerating);
        var pendingDeletion = ReadCounter(_pendingDeletion);
        return active.Keys
            .Where(name => !name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            .Select(name => new StorageSpacesVirtualDiskSample(
                name,
                NonNegative(active.GetValueOrDefault(name)),
                NonNegative(missing.GetValueOrDefault(name)),
                NonNegative(stale.GetValueOrDefault(name)),
                NonNegative(needRegeneration.GetValueOrDefault(name)),
                NonNegative(regenerating.GetValueOrDefault(name)),
                NonNegative(pendingDeletion.GetValueOrDefault(name))))
            .OrderBy(sample => sample.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Add(string path, out nint counter)
    {
        if (PdhAddEnglishCounter(_query, path, nint.Zero, out counter) != 0)
        {
            counter = nint.Zero;
        }
    }

    private static Dictionary<string, double> ReadCounter(nint counter)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == nint.Zero)
        {
            return result;
        }

        var size = 0u;
        var count = 0u;
        if (PdhGetFormattedCounterArray(
                counter,
                PdhFmtDouble | PdhFmtNoScale,
                ref size,
                ref count,
                nint.Zero) != PdhMoreData ||
            count == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (PdhGetFormattedCounterArray(
                    counter,
                    PdhFmtDouble | PdhFmtNoScale,
                    ref size,
                    ref count,
                    buffer) != 0)
            {
                return result;
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                    nint.Add(buffer, index * itemSize));
                var name = Marshal.PtrToStringUni(item.Name);
                if (!string.IsNullOrWhiteSpace(name) && item.Value.Status == 0)
                {
                    result[name] = item.Value.DoubleValue;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static double NonNegative(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;

    public void Dispose()
    {
        if (_query != nint.Zero)
        {
            PdhCloseQuery(_query);
            _query = nint.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(
        nint query,
        string fullCounterPath,
        nint userData,
        out nint counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(nint query);
}
