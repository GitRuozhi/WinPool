namespace WinPool.Application;

/// <summary>Automatic reports use separate progress keys from App-initiated refreshes.</summary>
public static class AutomaticInventoryNotification
{
    public static string ProgressKey(CollectionPurpose purpose) => $"inventory:automatic:{purpose}:scanning";

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
            AgentInventoryStartedEvent => WorkspaceNotificationFactory.ScanStarted(),
            AgentInventoryUpdatedEvent => WorkspaceNotificationFactory.ScanCompleted(string.Empty, report.OccurredAtUtc),
            _ => WorkspaceNotificationFactory.ScanFailed($"inventory:automatic:{purpose}:failed:{report.OccurredAtUtc.UtcTicks}")
        };
        return notification with
        {
            MessageTextKey = purpose == CollectionPurpose.Hardware ? "InventoryAutomaticHardware" : "InventoryAutomaticStorage",
            TitleTextKey = report is AgentInventoryFailedEvent ? "ScanFailed" : notification.TitleTextKey,
            OccurrenceKey = report is AgentInventoryStartedEvent ? ProgressKey(purpose.Value)
                : $"inventory:automatic:{purpose}:{notification.Code}:{report.OccurredAtUtc.UtcTicks}"
        };
    }
}
