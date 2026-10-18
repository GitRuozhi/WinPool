using System.Text.Json;
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
    private readonly object sync = new();
    private Task<string>? binding;

    public Task<string> ReadBindingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<string> current;
        lock (sync)
        {
            // One Agent incarnation owns one stable machine binding. Storage
            // identity and protection facts still get a fresh observation.
            if (binding is null || binding.IsFaulted) binding = ReadBindingAsync();
            current = binding;
        }
        return current.WaitAsync(cancellationToken);
    }

    private static async Task<string> ReadBindingAsync()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var machineGuid = (key?.GetValue("MachineGuid") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(machineGuid)
            || !Guid.TryParse(machineGuid, out var parsedMachineGuid)
            || parsedMachineGuid == Guid.Empty)
        {
            throw new InvalidDataException("The current Windows MachineGuid is unavailable.");
        }

        var raw = await WindowsPowerShellHost.Shared.QueryAsync(WindowsPowerShellQuery.MachineIdentity,
            new { }, CancellationToken.None).ConfigureAwait(false);
        using var result = JsonDocument.Parse(raw);
        var identifiers = result.RootElement.GetProperty("HardwareUuids").EnumerateArray()
            .Select(item => item.GetString()?.Trim())
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
