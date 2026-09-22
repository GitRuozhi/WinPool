using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class GlobalNotificationServiceTests
{
    [Fact]
    public void HistoryKeepsOnlyTheMostRecentTwoHundredIndependentEntries()
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
                    OccurrenceKey = "history:shared",
                    ShowNotification = false
                });
        }

        Assert.Equal(200, service.History.Count);
        Assert.Equal("1", service.History[0].Message);
        Assert.Equal("200", service.History[^1].Message);
        Assert.All(service.History, notification =>
            Assert.Equal("history:shared", notification.DeduplicationKey));
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void LongTextAndKeysAreBoundedWithoutCollapsingSeparateEvents()
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

        var defaultKeyService = new GlobalNotificationService();
        defaultKeyService.PublishInfo("Title", new string('x', 2_100) + "-first", "source");
        defaultKeyService.PublishInfo("Title", new string('x', 2_100) + "-second", "source");
        Assert.Equal(2, defaultKeyService.Notifications.Count);
    }

    [Fact]
    public void RepeatedCompletedOccurrenceKeyCreatesSeparateCardsAndHistoryEntries()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new GlobalNotificationService(clock);

        service.PublishWarning("First title", "First body", "inventory", "same");
        clock.Advance(TimeSpan.FromMinutes(1));
        service.PublishWarning("Second title", "Second body", "inventory", "same");

        Assert.Equal(2, service.Notifications.Count);
        Assert.Equal(2, service.History.Count);
        Assert.Equal("Second title", service.Notifications[0].Title);
        Assert.Equal("First title", service.Notifications[1].Title);
        Assert.NotEqual(service.Notifications[0].Id, service.Notifications[1].Id);
        Assert.Equal(["First body", "Second body"], service.History.Select(notification => notification.Message));
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
        service.PublishError("Failure once more", "Newest details", "inventory", "fault");
        service.ResolveByKey("fault");
        Assert.Empty(service.Notifications);
        Assert.All(service.History, notification => Assert.True(notification.IsResolved));

        service.PublishError("Still active", "Details", "inventory", "active");
        service.ClearHistory();
        Assert.Empty(service.History);
        Assert.Single(service.Notifications);
        service.ResolveByKey("active");
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void ProgressUpdatesByLifecycleKeyButNeverEntersHistory()
    {
        var service = new GlobalNotificationService();
        service.Publish(
            GlobalNotificationSeverity.Info,
            "Scanning",
            "One",
            "inventory",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "progress",
                IsProgress = true
            });
        var id = service.Notifications.Single().Id;
        service.Publish(
            GlobalNotificationSeverity.Info,
            "Scanning",
            "Two",
            "inventory",
            new GlobalNotificationOptions
            {
                OccurrenceKey = "progress",
                IsProgress = true
            });
        service.PublishError(
            "Failure",
            "Details",
            "inventory",
            "fault",
            autoDismiss: false);

        Assert.Single(service.History);
        Assert.Equal("fault", service.History.Single().DeduplicationKey);
        var progress = Assert.Single(service.Notifications, notification => notification.IsProgress);
        Assert.Equal(id, progress.Id);
        Assert.Equal("Two", progress.Message);
        Assert.False(progress.AutoDismiss);
        Assert.True(service.Notifications.Single(notification => notification.DeduplicationKey == "fault").AutoDismiss);
    }

    [Fact]
    public void NormalCardsExpireAfterEightSecondsAndErrorsAfterTwentySeconds()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new GlobalNotificationService(clock);
        service.PublishInfo("Info", "Short", "test", "info");
        service.PublishWarning("Warning", "Short", "test", "warning", autoDismiss: false);
        service.PublishError("Error", "Longer", "test", "error", autoDismiss: false);

        clock.Advance(TimeSpan.FromSeconds(8));
        service.DismissExpired();
        var remaining = Assert.Single(service.Notifications);
        Assert.Equal("error", remaining.DeduplicationKey);

        clock.Advance(TimeSpan.FromSeconds(12));
        service.DismissExpired();
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void ActiveCardsAreBoundedWithoutOverflowSummaryAndRetainHistory()
    {
        var service = new GlobalNotificationService(activeCapacity: 3);
        service.PublishInfo("One", "1", "test", "one");
        service.PublishInfo("Two", "2", "test", "two");
        service.PublishInfo("Three", "3", "test", "three");
        service.PublishError("Four", "4", "test", "four");

        Assert.Equal(3, service.Notifications.Count);
        Assert.Equal(["four", "three", "two"], service.Notifications.Select(notification => notification.DeduplicationKey));
        Assert.Equal(4, service.History.Count);
        Assert.DoesNotContain(service.Notifications, notification => notification.DeduplicationKey == "one");
    }

    [Fact]
    public void CodeSystemTargetAndDetailFlowIntoEveryRetainedMessage()
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
