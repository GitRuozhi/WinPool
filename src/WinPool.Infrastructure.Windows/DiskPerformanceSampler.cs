using System.Runtime.InteropServices;
using WinPool.Core;

namespace WinPool.Infrastructure.Windows;

public sealed record DiskPerformanceSample(
    string InstanceName,
    double ActivityPercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond);

public sealed class DiskPerformanceSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoScale = 0x00001000;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    private IntPtr _query;
    private IntPtr _activityCounter;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private bool _primed;

    public DiskPerformanceSampler()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\% Disk Time", IntPtr.Zero, out _activityCounter);
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\Disk Read Bytes/sec", IntPtr.Zero, out _readCounter);
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\Disk Write Bytes/sec", IntPtr.Zero, out _writeCounter);
        PdhCollectQueryData(_query);
    }

    public IReadOnlyList<DiskPerformanceSample> Sample()
    {
        if (_query == IntPtr.Zero)
        {
            return [];
        }

        if (PdhCollectQueryData(_query) != 0)
        {
            return [];
        }
        if (!_primed)
        {
            _primed = true;
            Thread.Sleep(500);
            if (PdhCollectQueryData(_query) != 0)
            {
                return [];
            }
        }

        var activity = ReadCounter(_activityCounter);
        var reads = ReadCounter(_readCounter);
        var writes = ReadCounter(_writeCounter);
        return activity.Keys
            .Where(x => !x.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            .Select(x => new DiskPerformanceSample(
                x,
                Math.Clamp(activity.GetValueOrDefault(x), 0, 100),
                reads.GetValueOrDefault(x),
                writes.GetValueOrDefault(x)))
            .OrderBy(x => x.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, double> ReadCounter(IntPtr counter)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == IntPtr.Zero)
        {
            return result;
        }

        var size = 0u;
        var count = 0u;
        if (PdhGetFormattedCounterArray(
                counter, PdhFmtDouble | PdhFmtNoScale, ref size, ref count, IntPtr.Zero) != PdhMoreData
            || count == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(
                    counter, PdhFmtDouble | PdhFmtNoScale, ref size, ref count, buffer) != 0)
            {
                return result;
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                    IntPtr.Add(buffer, i * itemSize));
                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                if (name.Length > 0)
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

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
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
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(
        IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
