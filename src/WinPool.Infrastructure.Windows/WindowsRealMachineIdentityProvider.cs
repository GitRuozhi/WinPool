using System.Management;
using Microsoft.Win32;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Builds the same machine binding for prepare and for every acceptance
/// recheck. A hostname or a value copied from a plan is insufficient.
/// </summary>
public sealed class WindowsRealMachineIdentityProvider : IRealMachineIdentityProvider
{
    public Task<string> ReadBindingAsync(CancellationToken cancellationToken) =>
        Task.Run(ReadBinding, cancellationToken);

    private static string ReadBinding()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var machineGuid = (key?.GetValue("MachineGuid") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(machineGuid)
            || !Guid.TryParse(machineGuid, out var parsedMachineGuid)
            || parsedMachineGuid == Guid.Empty)
        {
            throw new InvalidDataException("The current Windows MachineGuid is unavailable.");
        }

        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT UUID FROM Win32_ComputerSystemProduct");
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var identifiers = results.Cast<ManagementBaseObject>()
            .Select(item => item["UUID"]?.ToString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (identifiers.Length != 1
            || !Guid.TryParse(identifiers[0], out var parsedHardwareUuid)
            || parsedHardwareUuid == Guid.Empty
            || parsedHardwareUuid == Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
        {
            throw new InvalidDataException("The current machine hardware UUID is unavailable or ambiguous.");
        }

        return MachineBinding.Create(
        [
            parsedMachineGuid.ToString("D"),
            parsedHardwareUuid.ToString("D"),
            Environment.MachineName
        ]);
    }
}
