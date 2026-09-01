namespace WinPool.Ipc;

public static class AgentControlMessageTypes
{
    public const string HandshakeRequest = "agent.handshake.request";
    public const string HandshakeAccepted = "agent.handshake.accepted";
    public const string HandshakeRejected = "agent.handshake.rejected";
    public const string GetSnapshot = "agent.request.get_snapshot";
    public const string OpenMainWindow = "agent.request.open_main_window";
    public const string OpenNativeProperties = "agent.request.open_native_properties";
    public const string StartMonitoring = "agent.request.start_monitoring";
    public const string StopMonitoring = "agent.request.stop_monitoring";
    public const string LoadWorkspaceState =
        "agent.request.load_workspace_state";
    public const string SaveWorkspaceState =
        "agent.request.save_workspace_state";
    public const string ListSimulationDocuments =
        "agent.request.list_simulation_documents";
    public const string SaveSimulationDocument =
        "agent.request.save_simulation_document";
    public const string DeleteSimulationDocument =
        "agent.request.delete_simulation_document";
    public const string CommitSimulationEdit =
        "agent.request.commit_simulation_edit";
    public const string CaptureInventory = "agent.request.capture_inventory";
    public const string CaptureManageInventory =
        "agent.request.capture_manage_inventory";
    public const string LoadManageInventory =
        "agent.request.load_manage_inventory";
    public const string ExportMonitorCsv = "agent.request.export_monitor_csv";
    public const string Shutdown = "agent.request.shutdown";
    public const string Response = "agent.response";
}
