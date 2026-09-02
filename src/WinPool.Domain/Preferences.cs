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

public sealed record UserPreferences(
    ThemePreference Theme = ThemePreference.System,
    AccentColorPreference AccentColor = AccentColorPreference.System,
    LanguagePreference Language = LanguagePreference.SystemDefault,
    bool ShowHardwareIds = false,
    bool CreateMsrOnInitialize = true,
    long PartitionIgnoreSizeBytes = 8L * 1024 * 1024,
    bool ShowWelcomeAtStart = true,
    bool StartAgentAtLogin = false,
    bool ContinueMonitoringWhenUiCloses = false,
    bool HasShownWelcome = false,
    bool ContinuousMonitoringEnabled = false,
    double MonitoringSampleRateHz = 5,
    string LastActivePage = "Manage",
    int FormatVersion = 1);
