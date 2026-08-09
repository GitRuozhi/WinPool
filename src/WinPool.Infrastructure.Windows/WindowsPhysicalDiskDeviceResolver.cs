using System.Management;
using System.Runtime.InteropServices;

namespace WinPool.Infrastructure.Windows;

public interface IPhysicalDiskDeviceResolver
{
    string? ResolvePnpDeviceId(int diskNumber);
}

/// <summary>
/// Resolves one physical disk through a fixed, read-only WMI query. This is
/// intentionally narrower than the full WinPool inventory pipeline so opening
/// a native properties dialog does not wait for a machine-wide rescan.
/// </summary>
public sealed class WindowsPhysicalDiskDeviceResolver : IPhysicalDiskDeviceResolver
{
    public string? ResolvePnpDeviceId(int diskNumber)
    {
        if (diskNumber < 0)
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = {diskNumber}");
            using var results = searcher.Get();
            foreach (ManagementBaseObject result in results)
            {
                var deviceId = result["PNPDeviceID"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    return deviceId;
                }
            }
        }
        catch (Exception exception) when (
            exception is ManagementException
                or COMException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
        }

        return null;
    }
}
