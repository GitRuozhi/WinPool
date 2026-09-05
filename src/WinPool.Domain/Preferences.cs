namespace WinPool.Domain;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum AccentColorPreference
{
    System,
    Blue,
    Cyan,
    Green,
    Purple,
    Orange,
    Red
}

public enum LanguagePreference
{
    SystemDefault,
    ZhCn,
    EnUs
}

public enum StorageLocationMode
{
    Standard,
    Portable
}

/// <summary>
/// App-session preferences. Only the App writes them (app-settings.json);
/// the Agent may read them for tray presentation.
/// </summary>
public sealed record UserPreferences(
    ThemePreference Theme = ThemePreference.System,
    AccentColorPreference AccentColor = AccentColorPreference.System,
    LanguagePreference Language = LanguagePreference.SystemDefault,
    bool ShowHardwareIds = false,
    bool CreateMsrOnInitialize = true,
    long PartitionIgnoreSizeBytes = 8L * 1024 * 1024,
    string LastActivePage = "Manage",
    int FormatVersion = 1);

/// <summary>
/// Background preferences whose effect must survive App closure
/// (agent-settings.json). Only the Agent writes them. SavedAtUtc is a
/// content-generation label: consumers compare it by inequality, never by
/// ordering, so a system clock step can never hide a real change.
/// </summary>
public sealed record AgentPreferences(
    bool ContinuousMonitoringEnabled = false,
    double MonitoringSampleRateHz = 5,
    bool StartAgentAtLogin = false,
    DateTimeOffset SavedAtUtc = default,
    int FormatVersion = 1)
{
    public AgentPreferences StampSavedAtUtc(DateTimeOffset savedAtUtc) =>
        this with { SavedAtUtc = savedAtUtc };
}
