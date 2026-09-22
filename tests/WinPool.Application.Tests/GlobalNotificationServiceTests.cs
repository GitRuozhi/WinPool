using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class GlobalNotificationServiceTests
{
    [Fact]
    public void HistoryKeepsOnlyTheMostRecentTwoHundredEntries()
    {
        var service = new GlobalNotificationService();

        for (var index = 0; index < 201; index++)
        {
            service.Publish(
                GlobalNotificationSeverity.Info,
                "Recorded",
                index.ToString(),
                "test",
                new GlobalNotificationOptions
                {
                    OccurrenceKey = $"history:{index}",
                    ShowNotification = false
                });
        }

        Assert.Equal(200, service.History.Count);
        Assert.Equal("history:1", service.History[0].DeduplicationKey);
        Assert.Equal("history:200", service.History[^1].DeduplicationKey);
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void LongTextAndKeysAreBoundedWithoutMergingDifferentSemanticKeys()
    {
        var service = new GlobalNotificationService();
        var longTitle = new string('t', 320);
        var longMessage = new string('m', 2_400);
        var longDetail = new string('d', 2_400);
        var common = new string('k', 600);

        service.PublishError(
            longTitle,
            longMessage,
            "source",
            common + "-first",
            options: new GlobalNotificationOptions { Detail = longDetail });
        service.PublishError(
            longTitle,
            longMessage,
            "source",
            common + "-second",
            options: new GlobalNotificationOptions { Detail = longDetail });

        Assert.Equal(2, service.Notifications.Count);
        Assert.All(service.Notifications, notification =>
        {
            Assert.True(notification.Title.Length <= 256);
            Assert.True(notification.Message.Length <= 2048);
            Assert.True(notification.Detail.Length <= 2048);
            Assert.True(notification.DeduplicationKey.Length <= 512);
        });
        Assert.NotEqual(service.Notifications[0].DeduplicationKey, service.Notifications[1].DeduplicationKey);

        service.DismissByKey(common + "-first");
        Assert.Single(service.Notifications);
        service.ResolveByKey(common + "-second");
        Assert.Empty(service.Notifications);
        Assert.True(service.History.Single(notification => notification.IsResolved).IsResolved);

        var defaultKeyService = new GlobalNotificationService();
        defaultKeyService.PublishInfo("Title", new string('x', 2_100) + "-first", "source");
        defaultKeyService.PublishInfo("Title", new string('x', 2_100) + "-second", "source");
        Assert.Equal(2, defaultKeyService.Notifications.Count);
    }

    [Fact]
    public void RepeatedSemanticKeyUpdatesOneActiveAndOneHistoryRecord()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new GlobalNotificationService(clock);

        service.PublishWarning("First title", "First body", "inventory", "same");
        var initialId = service.Notifications.Single().Id;
        clock.Advance(TimeSpan.FromMinutes(1));
        service.PublishWarning("Updated title", "Updated body", "inventory", "same");

        var active = Assert.Single(service.Notifications);
        var history = Assert.Single(service.History);
        Assert.Equal(initialId, active.Id);
        Assert.Equal("Updated title", active.Title);
        Assert.Equal("Updated body", active.Message);
        Assert.Equal(2, active.OccurrenceCount);
        Assert.Equal(clock.GetUtcNow(), active.LastOccurredAt);
        Assert.Equal(2, history.OccurrenceCount);
        Assert.Equal(clock.GetUtcNow(), history.LastOccurredAt);
    }

    [Fact]
    public void DismissClearAndResolveHaveDifferentLifecycles()
    {
        var service = new GlobalNotificationService();
        service.PublishError("Failure", "Details", "inventory", "fault");

        var id = service.Notifications.Single().Id;
        service.Dismiss(id);
        Assert.Empty(service.Notifications);
        Assert.Single(service.History);
        Assert.False(service.History.Single().IsResolved);

        service.PublishError("Failure again", "New details", "inventory", "fault");
        service.ResolveByKey("fault");
        Assert.Empty(service.Notifications);
        Assert.True(service.History.Single().IsResolved);

        service.PublishError("Still active", "Details", "inventory", "active");
        service.ClearHistory();
        Assert.Empty(service.History);
        Assert.Single(service.Notifications);
        service.ResolveByKey("active");
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void ProgressIsExplicitlyExcludedFromHistoryAndErrorsAreStickyByDefault()
    {
        var service = new GlobalNotificationService();
        service.Publish(
            GlobalNotificationSeverity.Info,
            "Scanning",
            string.Empty,
            "inventory",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "progress",
                IsProgress = true,
                RecordInHistory = false,
                AutoDismiss = false
            });
        service.PublishError("Failure", "Details", "inventory", "fault");

        Assert.Single(service.History);
        Assert.Equal("fault", service.History.Single().DeduplicationKey);
        Assert.True(service.Notifications.Single(notification => notification.DeduplicationKey == "progress").IsProgress);
        Assert.False(service.Notifications.Single(notification => notification.DeduplicationKey == "fault").AutoDismiss);
        service.PublishWarning(
            "Sticky warning",
            "Details",
            "inventory",
            "sticky-warning",
            options: new GlobalNotificationOptions { AutoDismiss = false });
        Assert.False(service.Notifications.Single(notification => notification.DeduplicationKey == "sticky-warning").AutoDismiss);
    }

    [Fact]
    public void ShortNotificationsExpireAndPauseWithoutResettingOnDuplicate()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new GlobalNotificationService(
            clock,
            autoDismissDuration: TimeSpan.FromSeconds(10));
        service.PublishInfo("Short", "One", "test", "short");
        var id = service.Notifications.Single().Id;

        clock.Advance(TimeSpan.FromSeconds(4));
        service.PublishInfo("Short", "Repeated", "test", "short");
        clock.Advance(TimeSpan.FromSeconds(2));
        service.SetPaused(id, true);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.DismissExpired();
        Assert.Single(service.Notifications);

        service.SetPaused(id, false);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.DismissExpired();
        Assert.Single(service.Notifications);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.DismissExpired();
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void BoundedActiveQueueKeepsStickyWarningsAndSummarizesCompressedImportantNotifications()
    {
        var displayQueue = new GlobalNotificationService();
        for (var index = 0; index < 4; index++)
        {
            displayQueue.PublishError("Failure", index.ToString(), "test", $"display:{index}");
        }
        Assert.True(displayQueue.HasNotificationOverflow);
        Assert.Equal(1, displayQueue.OverflowedNotificationCount);

        var service = new GlobalNotificationService(activeCapacity: 3);
        service.PublishWarning("Warning 1", "Details", "test", "warning:1", autoDismiss: false);
        service.PublishWarning("Warning 2", "Details", "test", "warning:2", autoDismiss: false);
        service.PublishWarning("Warning 3", "Details", "test", "warning:3", autoDismiss: false);
        service.PublishError("New error", "Details", "test", "error:4");

        Assert.Equal(3, service.Notifications.Count);
        var summary = Assert.Single(service.Notifications.Where(notification => notification.IsOverflowSummary));
        Assert.Equal("notification.active-overflow", summary.Code);
        Assert.False(summary.AutoDismiss);
        Assert.True(summary.OccurrenceCount >= 2);
        Assert.Contains(service.Notifications, notification => notification.DeduplicationKey == "error:4");
        Assert.Equal(4, service.History.Count);
    }

    [Fact]
    public void CodeSystemTargetAndDetailFlowIntoTheRetainedMessage()
    {
        var service = new GlobalNotificationService();
        service.Publish(
            GlobalNotificationSeverity.Error,
            "Failure",
            "Readable explanation",
            "operation",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "operation:failure",
                Code = "operation.denied",
                SystemId = "local:machine",
                Target = "virtual-disk:1",
                Detail = "Copyable detail"
            });

        var notification = Assert.Single(service.History);
        Assert.Equal("operation.denied", notification.Code);
        Assert.Equal("local:machine", notification.SystemId);
        Assert.Equal("virtual-disk:1", notification.Target);
        Assert.Equal("Copyable detail", notification.Detail);
    }

    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
