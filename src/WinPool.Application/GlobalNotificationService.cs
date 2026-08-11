using System.Collections.ObjectModel;

namespace WinPool.Application;

public sealed class GlobalNotificationService : IGlobalNotificationService
{
    private readonly ObservableCollection<GlobalNotification> _notifications = [];

    public GlobalNotificationService()
    {
        Notifications = new ReadOnlyObservableCollection<GlobalNotification>(_notifications);
    }

    public ReadOnlyObservableCollection<GlobalNotification> Notifications { get; }

    public void PublishInfo(
        string title,
        string message,
        string source,
        string? occurrenceKey = null,
        bool autoDismiss = true) =>
        Publish(GlobalNotificationSeverity.Info, title, message, source, occurrenceKey, autoDismiss);

    public void PublishWarning(string title, string message, string source, string? occurrenceKey = null) =>
        Publish(GlobalNotificationSeverity.Warning, title, message, source, occurrenceKey, true);

    public void PublishError(string title, string message, string source, string? occurrenceKey = null) =>
        Publish(GlobalNotificationSeverity.Error, title, message, source, occurrenceKey, true);

    public void Dismiss(string id)
    {
        var notification = _notifications.FirstOrDefault(x => x.Id == id);
        if (notification is not null)
        {
            _notifications.Remove(notification);
        }
    }

    public void DismissByKey(string deduplicationKey)
    {
        var matches = _notifications
            .Where(x => x.DeduplicationKey.Equals(deduplicationKey, StringComparison.Ordinal))
            .ToList();
        foreach (var match in matches)
        {
            _notifications.Remove(match);
        }
    }

    private void Publish(
        GlobalNotificationSeverity severity,
        string title,
        string message,
        string source,
        string? occurrenceKey,
        bool autoDismiss)
    {
        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var deduplicationKey = occurrenceKey ?? $"{severity}:{source}:{title}:{message}";
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
            deduplicationKey,
            autoDismiss));
    }
}
