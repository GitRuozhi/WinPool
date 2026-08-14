using Microsoft.Win32;

namespace WinPool.App.Services;

internal sealed class AgentStartupRegistration
{
    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinPool";
    private const string LegacyAgentValueName = "WinPool.Agent";

    public string AgentExecutablePath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "Agent", "WinPool.Agent.exe"));

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return string.Equals(
            value,
            CommandValue(),
            StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && !File.Exists(AgentExecutablePath))
        {
            throw new FileNotFoundException(
                "The WinPool tray Agent executable is unavailable.",
                AgentExecutablePath);
        }

        using var key = Registry.CurrentUser.CreateSubKey(
            RunKey,
            writable: true)
            ?? throw new UnauthorizedAccessException(
                "The current-user startup registry key could not be opened.");
        if (enabled)
        {
            key.SetValue(
                ValueName,
                CommandValue(),
                RegistryValueKind.String);
            key.DeleteValue(LegacyAgentValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyAgentValueName, throwOnMissingValue: false);
        }
    }

    private string CommandValue() =>
        $"\"{AgentExecutablePath}\"";
}
