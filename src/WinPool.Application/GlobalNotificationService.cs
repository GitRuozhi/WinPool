using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace WinPool.Application;

/// <summary>
/// Owns the small, process-local notification state used by the desktop shell.
/// Every completed event gets its own retained history entry. Occurrence keys
/// only identify an in-progress lifecycle or a condition that has recovered;
/// they never merge separate user-visible events.
/// </summary>
public sealed class GlobalNotificationService : IGlobalNotificationService
{
    public const int DefaultHistoryCapacity = 200;
    public const int DefaultActiveCapacity = 3;
    public const int VisibleNotificationCapacity = DefaultActiveCapacity;

    private static readonly TimeSpan DefaultAutoDismissDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultErrorAutoDismissDuration = TimeSpan.FromSeconds(20);

    private readonly ObservableCollection<GlobalNotification> _notifications = [];
    private readonly ObservableCollection<GlobalNotification> _history = [];
    private readonly Dictionary<string, DateTimeOffset> lifetimes = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan autoDismissDuration;
    private readonly TimeSpan errorAutoDismissDuration;
    private readonly int historyCapacity;
    private readonly int activeCapacity;

    public GlobalNotificationService(
        TimeProvider? timeProvider = null,
        int historyCapacity = DefaultHistoryCapacity,
        int activeCapacity = DefaultActiveCapacity,
        TimeSpan? autoDismissDuration = null,
        TimeSpan? errorAutoDismissDuration = null)
    {
        if (historyCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        }

        if (activeCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(activeCapacity));
        }

        var normalDuration = autoDismissDuration ?? DefaultAutoDismissDuration;
        if (normalDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(autoDismissDuration));
        }

        var errorDuration = errorAutoDismissDuration ?? DefaultErrorAutoDismissDuration;
        if (errorDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(errorAutoDismissDuration));
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.historyCapacity = historyCapacity;
        this.activeCapacity = activeCapacity;
        this.autoDismissDuration = normalDuration;
        this.errorAutoDismissDuration = errorDuration;
        Notifications = new ReadOnlyObservableCollection<GlobalNotification>(_notifications);
        History = new ReadOnlyObservableCollection<GlobalNotification>(_history);
    }

    /// <summary>
    /// The three cards currently shown in the shell. When another message
    /// arrives, the oldest card yields while the complete session history keeps
    /// the event for the Developer page.
    /// </summary>
    public ReadOnlyObservableCollection<GlobalNotification> Notifications { get; }

    public ReadOnlyObservableCollection<GlobalNotification> History { get; }

    public void Publish(
        GlobalNotificationSeverity severity,
        string title,
        string message,
        string source,
        GlobalNotificationOptions? options = null)
    {
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        options ??= new GlobalNotificationOptions();
        var normalizedTitle = Bound(title, 256);
        var normalizedMessage = Bound(message, 2048);
        var normalizedDetail = Bound(options.Detail, 2048);
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            && string.IsNullOrWhiteSpace(normalizedMessage)
            && string.IsNullOrWhiteSpace(normalizedDetail))
        {
            return;
        }

        var normalizedSource = Bound(source, 128);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            normalizedSource = "application";
        }

        var normalizedCode = Bound(options.Code, 128);
        var normalizedSystemId = Bound(options.SystemId, 256);
        var normalizedTarget = Bound(options.Target, 512);
        var requestedKey = BoundKey(options.OccurrenceKey, 512);
        var occurrenceKey = string.IsNullOrWhiteSpace(requestedKey)
            ? CreateDefaultKey(severity, normalizedSource, options.Code, title, message, options.Detail)
            : requestedKey;
        if (string.IsNullOrWhiteSpace(occurrenceKey))
        {
            return;
        }

        var isProgress = options.IsProgress;
        // User-facing completed events must always leave the card stack on a
        // timer. A progress card is the one lifecycle exception: it stays only
        // until its caller reports completion, failure, cancellation, or an
        // unknown outcome using DismissByKey/ResolveByKey.
        var autoDismiss = !isProgress;
        var now = timeProvider.GetUtcNow();
        var notification = new GlobalNotification(
            Guid.NewGuid().ToString("N"),
            severity,
            normalizedTitle,
            normalizedMessage,
            normalizedSource,
            now,
            occurrenceKey,
            autoDismiss,
            normalizedCode,
            normalizedSystemId,
            normalizedTarget,
            normalizedDetail,
            IsProgress: isProgress);

        // Progress is excluded even if a caller accidentally leaves the
        // default RecordInHistory value in place.
        if (options.RecordInHistory && !isProgress)
        {
            AddHistory(notification);
        }

        if (options.ShowNotification)
        {
            if (isProgress)
            {
                UpdateProgress(notification);
            }
            else
            {
                AddTransient(notification);
            }
        }
    }

    public void PublishInfo(
        string title,
        string message,
        string source,
        string? occurrenceKey = null,
        bool autoDismiss = true,
        GlobalNotificationOptions? options = null) =>
        Publish(
            GlobalNotificationSeverity.Info,
            title,
            message,
            source,
            WithCompatibilityOptions(options, occurrenceKey, autoDismiss));

    public void PublishWarning(
        string title,
        string message,
        string source,
        string? occurrenceKey = null,
        bool autoDismiss = true,
        GlobalNotificationOptions? options = null) =>
        Publish(
            GlobalNotificationSeverity.Warning,
            title,
            message,
            source,
            WithCompatibilityOptions(options, occurrenceKey, autoDismiss));

    public void PublishError(
        string title,
        string message,
        string source,
        string? occurrenceKey = null,
        bool autoDismiss = true,
        GlobalNotificationOptions? options = null) =>
        Publish(
            GlobalNotificationSeverity.Error,
            title,
            message,
            source,
            WithCompatibilityOptions(options, occurrenceKey, autoDismiss));

    public void Dismiss(string id)
    {
        var index = FindActiveIndexById(id);
        if (index >= 0)
        {
            RemoveActiveAt(index);
        }
    }

    public void DismissByKey(string deduplicationKey)
    {
        if (string.IsNullOrWhiteSpace(deduplicationKey))
        {
            return;
        }

        var normalizedKey = BoundKey(deduplicationKey, 512);
        for (var index = _notifications.Count - 1; index >= 0; index--)
        {
            if (_notifications[index].DeduplicationKey.Equals(normalizedKey, StringComparison.Ordinal))
            {
                RemoveActiveAt(index);
            }
        }
    }

    public void ResolveByKey(string deduplicationKey)
    {
        if (string.IsNullOrWhiteSpace(deduplicationKey))
        {
            return;
        }

        var normalizedKey = BoundKey(deduplicationKey, 512);
        DismissByKey(normalizedKey);
        for (var index = 0; index < _history.Count; index++)
        {
            var history = _history[index];
            if (history.DeduplicationKey.Equals(normalizedKey, StringComparison.Ordinal)
                && !history.IsResolved)
            {
                _history[index] = history with { IsResolved = true };
            }
        }
    }

    public void ClearHistory() => _history.Clear();

    public void DismissExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in lifetimes.ToArray())
        {
            if (now >= entry.Value)
            {
                Dismiss(entry.Key);
            }
        }
    }

    private void AddHistory(GlobalNotification notification)
    {
        _history.Add(notification);
        while (_history.Count > historyCapacity)
        {
            _history.RemoveAt(0);
        }
    }

    private void UpdateProgress(GlobalNotification notification)
    {
        var existingIndex = FindProgressIndexByKey(notification.DeduplicationKey);
        if (existingIndex >= 0)
        {
            var previous = _notifications[existingIndex];
            _notifications[existingIndex] = notification with
            {
                Id = previous.Id,
                CreatedAt = previous.CreatedAt
            };
            return;
        }

        AddActive(notification);
    }

    private void AddTransient(GlobalNotification notification) => AddActive(notification);

    private void AddActive(GlobalNotification notification)
    {
        if (_notifications.Count >= activeCapacity)
        {
            RemoveActiveAt(FindOldestEvictionIndex());
        }

        // New cards appear first in the lower-right stack, so a burst of
        // independent messages never hides the newest event behind old cards.
        _notifications.Insert(0, notification);
        if (notification.AutoDismiss)
        {
            lifetimes[notification.Id] = timeProvider.GetUtcNow() + DurationFor(notification);
        }
    }

    private int FindOldestEvictionIndex()
    {
        // Preserve a visible in-progress lifecycle when a transient card can
        // yield instead. If all cards are progress cards, the oldest one gives
        // way rather than introducing an invisible active queue.
        for (var index = _notifications.Count - 1; index >= 0; index--)
        {
            if (!_notifications[index].IsProgress)
            {
                return index;
            }
        }

        return _notifications.Count - 1;
    }

    private TimeSpan DurationFor(GlobalNotification notification) =>
        notification.Severity == GlobalNotificationSeverity.Error
            ? errorAutoDismissDuration
            : autoDismissDuration;

    private void RemoveActiveAt(int index)
    {
        lifetimes.Remove(_notifications[index].Id);
        _notifications.RemoveAt(index);
    }

    private int FindActiveIndexById(string id)
    {
        for (var index = 0; index < _notifications.Count; index++)
        {
            if (_notifications[index].Id.Equals(id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindProgressIndexByKey(string key)
    {
        for (var index = 0; index < _notifications.Count; index++)
        {
            var notification = _notifications[index];
            if (notification.IsProgress
                && notification.DeduplicationKey.Equals(key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static GlobalNotificationOptions WithCompatibilityOptions(
        GlobalNotificationOptions? options,
        string? occurrenceKey,
        bool autoDismiss) =>
        (options ?? new GlobalNotificationOptions()) with
        {
            OccurrenceKey = occurrenceKey ?? options?.OccurrenceKey,
            AutoDismiss = options?.AutoDismiss ?? autoDismiss
        };

    private static string Bound(string? value, int maximum)
    {
        var normalized = Normalize(value);
        if (normalized.Length <= maximum)
        {
            return normalized;
        }

        var length = maximum;
        if (char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..length];
    }

    private static string BoundKey(string? value, int maximum)
    {
        var normalized = Normalize(value);
        if (normalized.Length <= maximum)
        {
            return normalized;
        }

        var suffix = $":{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).AsSpan(0, 8))}";
        var length = maximum - suffix.Length;
        if (char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..length] + suffix;
    }

    private static string CreateDefaultKey(
        GlobalNotificationSeverity severity,
        string source,
        string? code,
        string? title,
        string? message,
        string? detail) =>
        BoundKey(
            $"{severity}:{source}:{BoundKey(code, 128)}:{BoundKey(title, 128)}:{BoundKey(message, 256)}:{BoundKey(detail, 256)}",
            512);

    private static string Normalize(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
}
