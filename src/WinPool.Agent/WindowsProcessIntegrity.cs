using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinPool.Agent;

/// <summary>Reads the process token rather than trusting an IPC claim or image path.</summary>
public static class WindowsProcessIntegrity
{
    public static bool CanControl(int? clientLevel, int? serverLevel) =>
        clientLevel is >= 0 && serverLevel is >= 0 && clientLevel >= serverLevel;

    public static int? TryRead(int processId)
    {
        if (processId <= 0) { return null; }
        using var process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) { return null; }
        using (token)
        {
            // TokenIntegrityLevel = TOKEN_MANDATORY_LABEL, whose first field is SID_AND_ATTRIBUTES.
            _ = GetTokenInformation(token, 25, nint.Zero, 0, out var length);
            if (length < nint.Size || length > 64 * 1024) { return null; }
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _)) { return null; }
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                return sid.StartsWith("S-1-16-", StringComparison.Ordinal)
                    && int.TryParse(sid.AsSpan(7), out var level) ? level : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        nint information, int informationLength, out int returnLength);
}
