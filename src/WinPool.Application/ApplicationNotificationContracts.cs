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
    // Completed messages expire. A false value is reserved for a live progress
    // lifecycle, which is removed explicitly when that operation ends.
    bool AutoDismiss = true,
    bool RecordInHistory = true,
    bool IsProgress = false,
    bool ShowNotification = true,
    string SystemId = "",
    string Target = "");

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
            && IsBounded(notification.SystemId, 256, true)
            && IsBounded(notification.Target, 512, true)
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
    public static ApplicationNotification ScanStarted(string occurrenceKey = "inventory:scanning") =>
        Create(
            "workspace.scan.started",
            ApplicationNotificationSeverity.Information,
            "Scanning",
            string.Empty,
            string.Empty,
            "inventory",
            occurrenceKey,
            autoDismiss: false,
            recordInHistory: false,
            isProgress: true);

    public static ApplicationNotification ScanCompleted(
        string safeLastScanText,
        DateTimeOffset scannedAt,
        string? occurrenceKey = null) =>
        Create(
            "workspace.scan.completed",
            ApplicationNotificationSeverity.Information,
            "ScanComplete",
            string.Empty,
            safeLastScanText,
            "inventory",
            occurrenceKey ?? $"inventory:scan-complete:{scannedAt.UtcTicks}");

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
        bool autoDismiss = true,
        bool recordInHistory = true,
        bool isProgress = false,
        bool showNotification = true,
        string systemId = "",
        string target = "")
    {
        var notification = new ApplicationNotification(
            code,
            severity,
            titleTextKey,
            messageTextKey,
            userDetailText,
            source,
            occurrenceKey,
            autoDismiss,
            recordInHistory,
            isProgress,
            showNotification,
            systemId,
            target);
        if (!ApplicationNotificationValidator.IsValid(notification))
        {
            throw new ArgumentException("The application notification is invalid.");
        }
        return notification;
    }
}
