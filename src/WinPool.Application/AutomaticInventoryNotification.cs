namespace WinPool.Application;

/// <summary>Automatic reports use separate progress keys from App-initiated refreshes.</summary>
public static class AutomaticInventoryNotification
{
    public static string ProgressKey(CollectionPurpose purpose) => $"inventory:automatic:{purpose}:scanning";

    public static string CompletedKey(CollectionPurpose purpose) => $"inventory:automatic:{purpose}:completed";

    public static string FailedKey(CollectionPurpose purpose) => $"inventory:automatic:{purpose}:failed";

    /// <summary>Capture ended with an event gap, so its result remains unknown.</summary>
    public static string InterruptedKey(CollectionPurpose purpose) =>
        $"inventory:automatic:{purpose}:outcome-unknown";

    public static ApplicationNotification? FromEvent(AgentEvent report)
    {
        var purpose = report switch
        {
            AgentInventoryStartedEvent { IsAutomatic: true } started => started.Purpose,
            AgentInventoryUpdatedEvent { IsAutomatic: true } updated => updated.Purpose,
            AgentInventoryFailedEvent { IsAutomatic: true } failed => failed.Purpose,
            _ => (CollectionPurpose?)null
        };
        if (purpose is null) return null; // Manual refresh is notified by its request/response path, including transport failure.
        var notification = report switch
        {
            AgentInventoryStartedEvent => WorkspaceNotificationFactory.ScanStarted(ProgressKey(purpose.Value)),
            AgentInventoryUpdatedEvent => WorkspaceNotificationFactory.ScanCompleted(
                string.Empty,
                report.OccurredAtUtc,
                CompletedKey(purpose.Value)),
            _ => WorkspaceNotificationFactory.ScanFailed(FailedKey(purpose.Value))
        };
        return notification with
        {
            MessageTextKey = purpose == CollectionPurpose.Hardware ? "InventoryAutomaticHardware" : "InventoryAutomaticStorage",
            TitleTextKey = report is AgentInventoryFailedEvent ? "ScanFailed" : notification.TitleTextKey,
            Code = report is AgentInventoryFailedEvent failure ? failure.Code : notification.Code
        };
    }
}
