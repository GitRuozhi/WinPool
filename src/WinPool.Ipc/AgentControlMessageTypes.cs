namespace WinPool.Ipc;

public static class AgentControlMessageTypes
{
    public const string HandshakeRequest = "agent.handshake.request";
    public const string HandshakeAccepted = "agent.handshake.accepted";
    public const string HandshakeRejected = "agent.handshake.rejected";
    public const string GetSnapshot = "agent.request.get_snapshot";
    public const string GetDevelopmentDiagnostics =
        "agent.request.get_development_diagnostics";
    public const string OpenMainWindow = "agent.request.open_main_window";
    public const string OpenNativeProperties = "agent.request.open_native_properties";
    public const string StartMonitoring = "agent.request.start_monitoring";
    public const string StopMonitoring = "agent.request.stop_monitoring";
    public const string StartTest = "agent.request.start_test";
    public const string CancelTest = "agent.request.cancel_test";
    public const string GetTestResult = "agent.request.get_test_result";
    public const string ListTestRuns = "agent.request.list_test_runs";
    public const string ListUserTestPresets =
        "agent.request.list_user_test_presets";
    public const string SaveUserTestPreset =
        "agent.request.save_user_test_preset";
    public const string DeleteUserTestPreset =
        "agent.request.delete_user_test_preset";
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
    public const string PersistDiteLegacyImport =
        "agent.request.persist_dite_legacy_import";
    public const string ListDiteLegacyImports =
        "agent.request.list_dite_legacy_imports";
    public const string GetDiteLegacyImportSummary =
        "agent.request.get_dite_legacy_import_summary";
    public const string ExportTestRun = "agent.request.export_test_run";
    public const string CaptureInventory = "agent.request.capture_inventory";
    public const string CaptureManageInventory =
        "agent.request.capture_manage_inventory";
    public const string LoadManageInventory =
        "agent.request.load_manage_inventory";
    public const string DetectTool = "agent.request.detect_tool";
    public const string ConfigureToolPath = "agent.request.configure_tool_path";
    public const string InstallMsiTool = "agent.request.install_msi_tool";
    public const string ExportMonitorCsv = "agent.request.export_monitor_csv";
    public const string ReviewSystemSupport = "agent.request.review_system_support";
    public const string ExecuteSystemSupport = "agent.request.execute_system_support";
    public const string Shutdown = "agent.request.shutdown";
    public const string Response = "agent.response";
}
