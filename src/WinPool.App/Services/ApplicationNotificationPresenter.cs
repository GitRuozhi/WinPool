using WinPool.Application;

namespace WinPool.App.Services;

public sealed class ApplicationNotificationPresenter(
    IGlobalNotificationService notifications,
    LocalizationService localization)
{
    private readonly IGlobalNotificationService notifications =
        notifications ?? throw new ArgumentNullException(nameof(notifications));
    private readonly LocalizationService localization =
        localization ?? throw new ArgumentNullException(nameof(localization));

    public void Present(ApplicationNotification notification)
    {
        if (!ApplicationNotificationValidator.IsValid(notification))
        {
            throw new ArgumentException("The application notification is invalid.", nameof(notification));
        }

        var title = Localize(notification.TitleTextKey);
        var message = Join(Localize(notification.MessageTextKey), notification.UserDetailText);
        notifications.Publish(
            ToGlobalSeverity(notification.Severity),
            title,
            message,
            notification.Source,
            new GlobalNotificationOptions
            {
                OccurrenceKey = notification.OccurrenceKey,
                AutoDismiss = notification.AutoDismiss,
                RecordInHistory = notification.RecordInHistory,
                IsProgress = notification.IsProgress,
                ShowNotification = notification.ShowNotification,
                Code = notification.Code,
                SystemId = notification.SystemId,
                Target = notification.Target,
                Detail = notification.UserDetailText
            });
    }

    private static GlobalNotificationSeverity ToGlobalSeverity(ApplicationNotificationSeverity severity) => severity switch
    {
        ApplicationNotificationSeverity.Information => GlobalNotificationSeverity.Info,
        ApplicationNotificationSeverity.Warning => GlobalNotificationSeverity.Warning,
        ApplicationNotificationSeverity.Error => GlobalNotificationSeverity.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(severity))
    };

    private string Localize(string key) => string.IsNullOrWhiteSpace(key)
        ? string.Empty
        : localization[key];

    private static string Join(string message, string detail) =>
        (string.IsNullOrWhiteSpace(message), string.IsNullOrWhiteSpace(detail)) switch
        {
            (true, true) => string.Empty,
            (true, false) => detail.Trim(),
            (false, true) => message.Trim(),
            _ => $"{message.Trim()} {detail.Trim()}"
        };
}
