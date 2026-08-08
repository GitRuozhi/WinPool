namespace WinPool.Application;

public enum ApplicationNotificationSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>
/// UI-neutral notification intent. Text keys are localized by the presentation layer;
/// UserDetailText must already be safe for direct display and is never treated as markup.
/// </summary>
public sealed record ApplicationNotification(
    string Code,
    ApplicationNotificationSeverity Severity,
    string TitleTextKey,
    string MessageTextKey,
    string UserDetailText,
    string Source,
    string OccurrenceKey,
    bool AutoDismiss = true);

public static class ApplicationNotificationValidator
{
    public static bool IsValid(ApplicationNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return IsBounded(notification.Code, 128, false)
            && Enum.IsDefined(notification.Severity)
            && IsBounded(notification.TitleTextKey, 128, true)
            && IsBounded(notification.MessageTextKey, 128, true)
            && IsBounded(notification.UserDetailText, 2048, true)
            && IsBounded(notification.Source, 128, false)
            && IsBounded(notification.OccurrenceKey, 512, false)
            && (!string.IsNullOrWhiteSpace(notification.TitleTextKey)
                || !string.IsNullOrWhiteSpace(notification.MessageTextKey)
                || !string.IsNullOrWhiteSpace(notification.UserDetailText));
    }

    private static bool IsBounded(string? value, int maximum, bool allowEmpty) =>
        value is not null
        && value.Length <= maximum
        && (allowEmpty || !string.IsNullOrWhiteSpace(value));
}

public static class WorkspaceNotificationFactory
{
    public static ApplicationNotification ScanStarted() =>
        Create(
            "workspace.scan.started",
            ApplicationNotificationSeverity.Information,
            "Scanning",
            string.Empty,
            string.Empty,
            "inventory",
            "inventory:scanning",
            autoDismiss: false);

    public static ApplicationNotification ScanCompleted(
        string safeLastScanText,
        DateTimeOffset scannedAt) =>
        Create(
            "workspace.scan.completed",
            ApplicationNotificationSeverity.Information,
            "ScanComplete",
            string.Empty,
            safeLastScanText,
            "inventory",
            $"inventory:scan-complete:{scannedAt.UtcTicks}");

    public static ApplicationNotification ScanFailed(string occurrenceKey) =>
        Create(
            "workspace.scan.failed",
            ApplicationNotificationSeverity.Error,
            "Error",
            "ScanFailed",
            string.Empty,
            "inventory",
            occurrenceKey);

    public static ApplicationNotification ExportCompleted(string occurrenceKey) =>
        Create(
            "workspace.export.completed",
            ApplicationNotificationSeverity.Information,
            "Export",
            "Exported",
            string.Empty,
            "workspace-operation",
            occurrenceKey);

    public static ApplicationNotification ImportCompleted(string occurrenceKey) =>
        Create(
            "workspace.import.completed",
            ApplicationNotificationSeverity.Information,
            "Import",
            "ImportedSimulation",
            string.Empty,
            "workspace-operation",
            occurrenceKey);

    public static ApplicationNotification OperationFailed(string occurrenceKey) =>
        Create(
            "workspace.operation.failed",
            ApplicationNotificationSeverity.Error,
            "Error",
            "OperationFailed",
            string.Empty,
            "workspace-operation",
            occurrenceKey);

    private static ApplicationNotification Create(
        string code,
        ApplicationNotificationSeverity severity,
        string titleTextKey,
        string messageTextKey,
        string userDetailText,
        string source,
        string occurrenceKey,
        bool autoDismiss = true)
    {
        var notification = new ApplicationNotification(
            code,
            severity,
            titleTextKey,
            messageTextKey,
            userDetailText,
            source,
            occurrenceKey,
            autoDismiss);
        if (!ApplicationNotificationValidator.IsValid(notification))
        {
            throw new ArgumentException("The application notification is invalid.");
        }
        return notification;
    }
}
