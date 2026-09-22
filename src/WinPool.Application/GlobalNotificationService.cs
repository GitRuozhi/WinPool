using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace WinPool.Application;

/// <summary>
/// Owns the small, process-local notification state used by the desktop shell.
/// It retains only strings and value types: callers log an exception separately
/// rather than handing the exception or a UI element to this service.
/// </summary>
public sealed class GlobalNotificationService : IGlobalNotificationService
{
    public const int DefaultHistoryCapacity = 200;
    public const int DefaultActiveCapacity = 200;
    public const int VisibleNotificationCapacity = 3;

    private const string ActiveOverflowKey = "notification:active-overflow";
    private const string ActiveOverflowCode = "notification.active-overflow";
    private static readonly TimeSpan DefaultAutoDismissDuration = TimeSpan.FromSeconds(8);

    private readonly ObservableCollection<GlobalNotification> _notifications = [];
    private readonly ObservableCollection<GlobalNotification> _history = [];
    private readonly Dictionary<string, LifetimeState> lifetimes = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan autoDismissDuration;
    private readonly int historyCapacity;
    private readonly int activeCapacity;

    public GlobalNotificationService(
        TimeProvider? timeProvider = null,
        int historyCapacity = DefaultHistoryCapacity,
        int activeCapacity = DefaultActiveCapacity,
        TimeSpan? autoDismissDuration = null)
    {
        if (historyCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        }

        // The overflow summary needs one slot while a newly arriving important
        // notification takes another.
        if (activeCapacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(activeCapacity));
        }

        var duration = autoDismissDuration ?? DefaultAutoDismissDuration;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(autoDismissDuration));
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.historyCapacity = historyCapacity;
        this.activeCapacity = activeCapacity;
        this.autoDismissDuration = duration;
        Notifications = new ReadOnlyObservableCollection<GlobalNotification>(_notifications);
        History = new ReadOnlyObservableCollection<GlobalNotification>(_history);
    }

    /// <summary>
    /// Active notifications. The collection is bounded independently from
    /// History; the shell renders its first three items and can show the rest
    /// in a local overflow view without relying on developer mode.
    /// </summary>
    public ReadOnlyObservableCollection<GlobalNotification> Notifications { get; }

    public ReadOnlyObservableCollection<GlobalNotification> History { get; }

    public int OverflowedNotificationCount => Math.Max(0, _notifications.Count - VisibleNotificationCapacity);

    public bool HasNotificationOverflow => OverflowedNotificationCount > 0;

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
        var deduplicationKey = string.IsNullOrWhiteSpace(requestedKey)
            ? CreateDefaultKey(severity, normalizedSource, options.Code, title, message, options.Detail)
            : requestedKey;
        if (string.IsNullOrWhiteSpace(deduplicationKey))
        {
            return;
        }

        var isProgress = options.IsProgress;
        var autoDismiss = isProgress
            ? false
            : options.AutoDismiss ?? severity != GlobalNotificationSeverity.Error;
        var now = timeProvider.GetUtcNow();
        var notification = new GlobalNotification(
            Guid.NewGuid().ToString("N"),
            severity,
            normalizedTitle,
            normalizedMessage,
            normalizedSource,
            now,
            deduplicationKey,
            autoDismiss,
            normalizedCode,
            normalizedSystemId,
            normalizedTarget,
            normalizedDetail,
            OccurrenceCount: 1,
            LastOccurredAt: now,
            IsProgress: isProgress);

        // Progress is explicitly excluded even when a caller accidentally
        // leaves RecordInHistory at its default value.
        if (options.RecordInHistory && !isProgress)
        {
            UpdateHistory(notification);
        }

        if (options.ShowNotification)
        {
            UpdateActive(notification);
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
        bool autoDismiss = false,
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
            if (history.DeduplicationKey.Equals(normalizedKey, StringComparison.Ordinal))
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
            var state = entry.Value;
            if (state.IsPaused || now < state.ExpiresAt)
            {
                continue;
            }

            Dismiss(entry.Key);
        }
    }

    public void SetPaused(string id, bool isPaused)
    {
        if (string.IsNullOrWhiteSpace(id) || !lifetimes.TryGetValue(id, out var state))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (isPaused == state.IsPaused)
        {
            return;
        }

        if (isPaused)
        {
            state.PausedRemaining = now >= state.ExpiresAt
                ? TimeSpan.Zero
                : state.ExpiresAt - now;
            state.IsPaused = true;
            return;
        }

        state.ExpiresAt = now + state.PausedRemaining;
        state.IsPaused = false;
    }

    private void UpdateHistory(GlobalNotification notification)
    {
        var index = FindHistoryIndexByKey(notification.DeduplicationKey);
        if (index < 0)
        {
            _history.Add(notification);
        }
        else
        {
            var previous = _history[index];
            var updated = MergeRepeated(previous, notification) with
            {
                Id = previous.Id,
                CreatedAt = previous.CreatedAt,
                IsResolved = false
            };
            _history.RemoveAt(index);
            _history.Add(updated);
        }

        while (_history.Count > historyCapacity)
        {
            _history.RemoveAt(0);
        }
    }

    private void UpdateActive(GlobalNotification notification)
    {
        var existingIndex = FindActiveIndexByKey(notification.DeduplicationKey);
        if (existingIndex >= 0)
        {
            var previous = _notifications[existingIndex];
            var updated = MergeRepeated(previous, notification) with
            {
                Id = previous.Id,
                CreatedAt = previous.CreatedAt,
                IsResolved = false
            };
            _notifications[existingIndex] = updated;
            if (!updated.AutoDismiss)
            {
                lifetimes.Remove(updated.Id);
            }
            else if (!lifetimes.ContainsKey(updated.Id))
            {
                // A short notification only gets a fresh deadline on its first
                // active appearance. Repeated updates deliberately keep its old
                // deadline so a noisy warning cannot occupy a card forever.
                lifetimes[updated.Id] = new LifetimeState(timeProvider.GetUtcNow() + autoDismissDuration);
            }
            return;
        }

        if (_notifications.Count < activeCapacity)
        {
            AddActive(notification);
            return;
        }

        AddAtCapacity(notification);
    }

    private void AddAtCapacity(GlobalNotification notification)
    {
        var summaryIndex = FindActiveIndexByKey(ActiveOverflowKey);
        var nonImportantCandidate = FindEvictionCandidate(summaryIndex, onlyNonImportant: true);

        if (!IsImportant(notification))
        {
            // A short, non-important card may yield to a newer one. It is
            // already in bounded History; it never displaces a sticky warning
            // or any error.
            if (nonImportantCandidate >= 0)
            {
                ReplaceActiveAt(nonImportantCandidate, notification);
            }
            return;
        }

        if (nonImportantCandidate >= 0)
        {
            ReplaceActiveAt(nonImportantCandidate, notification);
            return;
        }

        if (summaryIndex >= 0)
        {
            var importantCandidate = FindEvictionCandidate(summaryIndex, onlyNonImportant: false);
            if (importantCandidate >= 0)
            {
                ReplaceActiveAt(importantCandidate, notification);
                UpdateOverflowSummary(summaryIndex, additionalCompressedCount: 1);
            }
            return;
        }

        // The active set is entirely important conditions. Reserve one slot for a
        // sticky summary and one for the incoming condition rather than silently
        // dropping either condition. The summary truthfully says that details
        // were compressed once the bounded active set filled.
        var firstImportant = FindEvictionCandidate(excludedIndex: -1, onlyNonImportant: false);
        var secondImportant = FindEvictionCandidate(excludedIndex: firstImportant, onlyNonImportant: false);
        if (firstImportant < 0 || secondImportant < 0)
        {
            return;
        }

        ReplaceActiveAt(firstImportant, CreateOverflowSummary(compressedCount: 2));
        ReplaceActiveAt(secondImportant, notification);
    }

    private void AddActive(GlobalNotification notification)
    {
        _notifications.Add(notification);
        AddLifetime(notification);
    }

    private void ReplaceActiveAt(int index, GlobalNotification replacement)
    {
        var previous = _notifications[index];
        lifetimes.Remove(previous.Id);
        _notifications[index] = replacement;
        AddLifetime(replacement);
    }

    private void RemoveActiveAt(int index)
    {
        lifetimes.Remove(_notifications[index].Id);
        _notifications.RemoveAt(index);
    }

    private void AddLifetime(GlobalNotification notification)
    {
        if (notification.AutoDismiss)
        {
            lifetimes[notification.Id] = new LifetimeState(timeProvider.GetUtcNow() + autoDismissDuration);
        }
    }

    private int FindEvictionCandidate(int excludedIndex, bool onlyNonImportant)
    {
        var bestIndex = -1;
        for (var index = 0; index < _notifications.Count; index++)
        {
            if (index == excludedIndex || _notifications[index].IsOverflowSummary)
            {
                continue;
            }

            var candidate = _notifications[index];
            if (onlyNonImportant && IsImportant(candidate))
            {
                continue;
            }

            if (bestIndex < 0 || IsBetterEvictionCandidate(candidate, _notifications[bestIndex]))
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static bool IsBetterEvictionCandidate(GlobalNotification candidate, GlobalNotification current)
    {
        if (IsImportant(candidate) != IsImportant(current))
        {
            return !IsImportant(candidate);
        }

        return candidate.Severity < current.Severity
            || (candidate.Severity == current.Severity && candidate.AutoDismiss && !current.AutoDismiss)
            || (candidate.Severity == current.Severity
                && candidate.AutoDismiss == current.AutoDismiss
                && candidate.CreatedAt < current.CreatedAt);
    }

    private void UpdateOverflowSummary(int index, int additionalCompressedCount)
    {
        var existing = _notifications[index];
        var now = timeProvider.GetUtcNow();
        var count = SaturatingAdd(existing.OccurrenceCount, additionalCompressedCount);
        _notifications[index] = existing with
        {
            OccurrenceCount = count,
            LastOccurredAt = now,
            Detail = OverflowDetail(count)
        };
    }

    private GlobalNotification CreateOverflowSummary(int compressedCount)
    {
        var now = timeProvider.GetUtcNow();
        return new GlobalNotification(
            Guid.NewGuid().ToString("N"),
            GlobalNotificationSeverity.Error,
            "Additional important notifications",
            "Some active notifications were condensed because the active queue reached its limit.",
            "notification",
            now,
            ActiveOverflowKey,
            AutoDismiss: false,
            Code: ActiveOverflowCode,
            Detail: OverflowDetail(compressedCount),
            OccurrenceCount: compressedCount,
            LastOccurredAt: now,
            IsOverflowSummary: true);
    }

    private static string OverflowDetail(int compressedCount) =>
        $"{compressedCount} important notification(s) were condensed. Current active messages and retained session history may contain more detail.";

    private static GlobalNotification MergeRepeated(GlobalNotification previous, GlobalNotification next) =>
        next with
        {
            Severity = (GlobalNotificationSeverity)Math.Max((int)previous.Severity, (int)next.Severity),
            AutoDismiss = previous.AutoDismiss && next.AutoDismiss,
            OccurrenceCount = SaturatingAdd(previous.OccurrenceCount, 1),
            IsResolved = false
        };

    private static int SaturatingAdd(int value, int increment) =>
        value >= int.MaxValue - increment ? int.MaxValue : value + increment;

    private static GlobalNotificationOptions WithCompatibilityOptions(
        GlobalNotificationOptions? options,
        string? occurrenceKey,
        bool autoDismiss) =>
        (options ?? new GlobalNotificationOptions()) with
        {
            OccurrenceKey = occurrenceKey ?? options?.OccurrenceKey,
            AutoDismiss = options?.AutoDismiss ?? autoDismiss
        };

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

    private int FindActiveIndexByKey(string key)
    {
        for (var index = 0; index < _notifications.Count; index++)
        {
            if (_notifications[index].DeduplicationKey.Equals(key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindHistoryIndexByKey(string key)
    {
        for (var index = 0; index < _history.Count; index++)
        {
            if (_history[index].DeduplicationKey.Equals(key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

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

    private static bool IsImportant(GlobalNotification notification) =>
        !notification.IsProgress
        && (notification.Severity == GlobalNotificationSeverity.Error || !notification.AutoDismiss);

    private sealed class LifetimeState(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;

        public TimeSpan PausedRemaining { get; set; }

        public bool IsPaused { get; set; }
    }
}
