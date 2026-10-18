


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
        if (diskNumber < 0) return null;
        try
        {
            var json = WindowsPowerShellHost.Shared.QueryAsync(WindowsPowerShellQuery.PhysicalDevice,
                new { DiskNumber = diskNumber }, CancellationToken.None).GetAwaiter().GetResult();
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.GetProperty("DeviceId").GetString()?.Trim();
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException)
        { return null; }
    }
}