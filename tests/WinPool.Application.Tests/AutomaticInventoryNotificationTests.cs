using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class AutomaticInventoryNotificationTests
{
    [Theory]
    [InlineData(CollectionPurpose.Storage, "InventoryAutomaticStorage")]
    [InlineData(CollectionPurpose.Hardware, "InventoryAutomaticHardware")]
    public void AutomaticStagesUseSharedStatusAndSeparateProgressKeys(CollectionPurpose purpose, string messageKey)
    {
        var now = DateTimeOffset.UtcNow;
        var started = AutomaticInventoryNotification.FromEvent(new AgentInventoryStartedEvent(purpose, now, true))!;
        var success = AutomaticInventoryNotification.FromEvent(new AgentInventoryUpdatedEvent(purpose, null!, now, true))!;
        var failure = AutomaticInventoryNotification.FromEvent(new AgentInventoryFailedEvent(purpose, "test.failure", now, true))!;
        Assert.Equal(WorkspaceNotificationFactory.ScanStarted().TitleTextKey, started.TitleTextKey);
        Assert.Equal(WorkspaceNotificationFactory.ScanCompleted("", now).TitleTextKey, success.TitleTextKey);
        Assert.Equal("ScanFailed", failure.TitleTextKey);
        Assert.False(started.AutoDismiss);
        Assert.True(started.IsProgress);
        Assert.False(started.RecordInHistory);
        Assert.True(success.AutoDismiss);
        Assert.Equal(ApplicationNotificationSeverity.Error, failure.Severity);
        Assert.True(failure.AutoDismiss);
        Assert.Equal(AutomaticInventoryNotification.ProgressKey(purpose), started.OccurrenceKey);
        Assert.Equal(AutomaticInventoryNotification.CompletedKey(purpose), success.OccurrenceKey);
        Assert.Equal(AutomaticInventoryNotification.FailedKey(purpose), failure.OccurrenceKey);
        Assert.Equal("test.failure", failure.Code);
        Assert.NotEqual(WorkspaceNotificationFactory.ScanStarted().OccurrenceKey, started.OccurrenceKey);
        foreach (var notification in new[] { started, success, failure })
        {
            Assert.Equal(messageKey, notification.MessageTextKey);
            Assert.True(ApplicationNotificationValidator.IsValid(notification));
        }
        Assert.NotEqual(started.OccurrenceKey, success.OccurrenceKey);
        Assert.NotEqual(started.OccurrenceKey, failure.OccurrenceKey);
    }

    [Theory]
    [InlineData(CollectionPurpose.Storage)]
    [InlineData(CollectionPurpose.Hardware)]
    public void ManualReportsDoNotDuplicateRequestResponseNotifications(CollectionPurpose purpose)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(AutomaticInventoryNotification.FromEvent(new AgentInventoryStartedEvent(purpose, now)));
        Assert.Null(AutomaticInventoryNotification.FromEvent(new AgentInventoryUpdatedEvent(purpose, null!, now)));
        Assert.Null(AutomaticInventoryNotification.FromEvent(new AgentInventoryFailedEvent(purpose, "test.failure", now)));
        Assert.Null(AutomaticInventoryNotification.FromEvent(new AgentStateReseedEvent(null!, "test.reseed", now)));
        Assert.NotEqual(AutomaticInventoryNotification.ProgressKey(CollectionPurpose.Storage),
            AutomaticInventoryNotification.ProgressKey(CollectionPurpose.Hardware));
        Assert.NotEqual(AutomaticInventoryNotification.ProgressKey(purpose),
            AutomaticInventoryNotification.InterruptedKey(purpose));
    }
}
