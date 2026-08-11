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
        switch (notification.Severity)
        {
            case ApplicationNotificationSeverity.Information:
                notifications.PublishInfo(
                    title,
                    message,
                    notification.Source,
                    notification.OccurrenceKey,
                    notification.AutoDismiss);
                break;
            case ApplicationNotificationSeverity.Warning:
                notifications.PublishWarning(
                    title,
                    message,
                    notification.Source,
                    notification.OccurrenceKey);
                break;
            case ApplicationNotificationSeverity.Error:
                notifications.PublishError(
                    title,
                    message,
                    notification.Source,
                    notification.OccurrenceKey);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(notification));
        }
    }

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
