using System.Collections.ObjectModel;

namespace WinPool.Core;

public sealed class GlobalNotificationService : IGlobalNotificationService
{
    private readonly ObservableCollection<GlobalNotification> _notifications = [];

    public GlobalNotificationService()
    {
        Notifications = new ReadOnlyObservableCollection<GlobalNotification>(_notifications);
    }

    public ReadOnlyObservableCollection<GlobalNotification> Notifications { get; }

    public void PublishWarning(string title, string message, string source, string? occurrenceKey = null) =>
        Publish(GlobalNotificationSeverity.Warning, title, message, source, occurrenceKey);

    public void PublishError(string title, string message, string source, string? occurrenceKey = null) =>
        Publish(GlobalNotificationSeverity.Error, title, message, source, occurrenceKey);

    public void Dismiss(string id)
    {
        var notification = _notifications.FirstOrDefault(x => x.Id == id);
        if (notification is not null)
        {
            _notifications.Remove(notification);
        }
    }

    private void Publish(
        GlobalNotificationSeverity severity,
        string title,
        string message,
        string source,
        string? occurrenceKey)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var deduplicationKey = occurrenceKey ?? $"{severity}:{source}:{message}";
        if (_notifications.Any(x => x.DeduplicationKey.Equals(
                deduplicationKey,
                StringComparison.Ordinal)))
        {
            return;
        }

        _notifications.Add(new GlobalNotification(
            Guid.NewGuid().ToString("N"),
            severity,
            title,
            message,
            source,
            DateTimeOffset.Now,
            deduplicationKey));
    }
}
