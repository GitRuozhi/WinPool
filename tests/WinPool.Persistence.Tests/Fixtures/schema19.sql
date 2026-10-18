CREATE TABLE IF NOT EXISTS schema_info(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    schema_version INTEGER NOT NULL,
    applied_at_utc_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS workspace_state(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    json TEXT NOT NULL,
    updated_at_utc_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS systems(
    system_id TEXT PRIMARY KEY,
    kind INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    machine_binding_hash TEXT,
    created_at_utc_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS inventory_snapshots(
    snapshot_id TEXT PRIMARY KEY,
    system_id TEXT NOT NULL REFERENCES systems(system_id),
    inventory_version TEXT NOT NULL,
    captured_at_utc_ms INTEGER NOT NULL,
    provider_kind INTEGER NOT NULL,
    sanitized_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_inventory_snapshots_system_time
    ON inventory_snapshots(system_id, captured_at_utc_ms DESC);
CREATE TABLE IF NOT EXISTS local_inventory_document(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id),
    document_id TEXT NOT NULL,
    document_schema_version INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    sanitized_json TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    captured_at_utc_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS storage_objects(
    snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id) ON DELETE CASCADE,
    object_id TEXT NOT NULL,
    object_kind INTEGER NOT NULL,
    provider_key_hash TEXT NOT NULL,
    sanitized_json TEXT NOT NULL,
    PRIMARY KEY(snapshot_id, object_id)
);
CREATE TABLE IF NOT EXISTS storage_relationships(
    snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id) ON DELETE CASCADE,
    from_object_id TEXT NOT NULL,
    to_object_id TEXT NOT NULL,
    relationship_kind TEXT NOT NULL,
    PRIMARY KEY(snapshot_id, from_object_id, to_object_id, relationship_kind)
);
CREATE TABLE IF NOT EXISTS operation_plans(
    operation_id TEXT PRIMARY KEY,
    plan_hash TEXT NOT NULL UNIQUE,
    environment_id TEXT NOT NULL,
    risk INTEGER NOT NULL,
    state INTEGER NOT NULL,
    sanitized_json TEXT NOT NULL,
    created_at_utc_ms INTEGER NOT NULL,
    authorization_digest TEXT,
    accepted_at_utc_ms INTEGER,
    preparation_id TEXT,
    preparation_intent_hash TEXT
);
CREATE UNIQUE INDEX ix_operation_plans_preparation_id
    ON operation_plans(preparation_id);
CREATE TABLE IF NOT EXISTS operation_steps(
    operation_id TEXT NOT NULL REFERENCES operation_plans(operation_id) ON DELETE CASCADE,
    step_id TEXT NOT NULL,
    sequence_no INTEGER NOT NULL,
    state INTEGER NOT NULL,
    sanitized_json TEXT NOT NULL,
    target_json TEXT,
    evidence_json TEXT,
    updated_at_utc_ms INTEGER,
    PRIMARY KEY(operation_id, step_id)
);
CREATE TABLE IF NOT EXISTS execution_events(
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    operation_id TEXT NOT NULL REFERENCES operation_plans(operation_id),
    timestamp_utc_ms INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    code TEXT NOT NULL,
    sanitized_message TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_execution_events_operation_time
    ON execution_events(operation_id, timestamp_utc_ms);
CREATE TABLE IF NOT EXISTS simulation_documents(
    document_id TEXT PRIMARY KEY,
    document_schema_version INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    sanitized_json TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    revision INTEGER NOT NULL,
    created_at_utc_ms INTEGER NOT NULL,
    updated_at_utc_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS simulation_edit_commits(
    operation_id TEXT PRIMARY KEY REFERENCES operation_plans(operation_id),
    commit_id TEXT NOT NULL UNIQUE,
    document_id TEXT NOT NULL,
    before_sha256 TEXT NOT NULL,
    after_sha256 TEXT NOT NULL,
    document_revision INTEGER NOT NULL,
    document_schema_version INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    sanitized_json TEXT NOT NULL,
    updated_at_utc_ms INTEGER NOT NULL,
    committed_at_utc_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_simulation_edit_commits_document_revision
    ON simulation_edit_commits(document_id, document_revision DESC);
CREATE TABLE IF NOT EXISTS monitor_sessions(
    session_id TEXT PRIMARY KEY,
    started_at_utc_ms INTEGER NOT NULL,
    ended_at_utc_ms INTEGER,
    clock_source TEXT NOT NULL,
    state INTEGER NOT NULL,
    dropped_samples INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS monitor_devices(
    session_id TEXT NOT NULL REFERENCES monitor_sessions(session_id) ON DELETE CASCADE,
    device_id TEXT NOT NULL,
    sanitized_name TEXT NOT NULL,
    source_kind INTEGER NOT NULL,
    PRIMARY KEY(session_id, device_id)
);
CREATE TABLE IF NOT EXISTS monitor_samples(
    session_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    timestamp_utc_ms INTEGER NOT NULL,
    activity_pct REAL,
    read_bytes_per_sec REAL,
    write_bytes_per_sec REAL,
    read_operations_per_sec REAL,
    write_operations_per_sec REAL,
    queue_length REAL,
    average_latency_ms REAL,
    cpu_pct REAL,
    virtual_disk_active_bytes REAL,
    virtual_disk_missing_bytes REAL,
    virtual_disk_stale_bytes REAL,
    virtual_disk_need_regeneration_bytes REAL,
    virtual_disk_regenerating_bytes REAL,
    virtual_disk_pending_deletion_bytes REAL,
    FOREIGN KEY(session_id, device_id)
        REFERENCES monitor_devices(session_id, device_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_monitor_samples_session_time
    ON monitor_samples(session_id, timestamp_utc_ms);
CREATE INDEX IF NOT EXISTS ix_monitor_samples_session_device_time
    ON monitor_samples(session_id, device_id, timestamp_utc_ms);
CREATE TABLE IF NOT EXISTS storage_health_events(
    event_key TEXT PRIMARY KEY,
    channel TEXT NOT NULL,
    provider TEXT NOT NULL,
    record_id INTEGER,
    windows_event_id INTEGER NOT NULL,
    severity INTEGER NOT NULL,
    occurred_at_utc_ms INTEGER NOT NULL,
    message TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_storage_health_events_time
    ON storage_health_events(occurred_at_utc_ms, event_key);
CREATE TABLE IF NOT EXISTS monitor_rollups(
    session_id TEXT NOT NULL REFERENCES monitor_sessions(session_id) ON DELETE CASCADE,
    device_id TEXT NOT NULL,
    bucket_start_utc_ms INTEGER NOT NULL,
    bucket_width_ms INTEGER NOT NULL,
    sample_count INTEGER NOT NULL,
    sanitized_json TEXT NOT NULL,
    PRIMARY KEY(session_id, device_id, bucket_start_utc_ms, bucket_width_ms)
);
CREATE TABLE IF NOT EXISTS inventory_comparisons(
    comparison_id TEXT PRIMARY KEY,
    reference_snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id),
    candidate_snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id),
    sanitized_json TEXT NOT NULL,
    created_at_utc_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS agent_sessions(
    session_id TEXT PRIMARY KEY,
    process_id INTEGER NOT NULL,
    started_at_utc_ms INTEGER NOT NULL,
    ended_at_utc_ms INTEGER,
    shutdown_clean INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS worker_processes(
    process_instance_id TEXT PRIMARY KEY,
    process_id INTEGER NOT NULL,
    agent_session_id TEXT NOT NULL REFERENCES agent_sessions(session_id),
    process_kind INTEGER NOT NULL,
    correlation_id TEXT NOT NULL,
    started_at_utc_ms INTEGER NOT NULL,
    last_heartbeat_utc_ms INTEGER NOT NULL,
    state INTEGER NOT NULL,
    owns_job_object INTEGER NOT NULL DEFAULT 0,
    shutdown_deadline_utc_ms INTEGER
);
CREATE INDEX IF NOT EXISTS ix_worker_processes_live_pid
    ON worker_processes(process_id, state);
CREATE TABLE operation_capacity_attempts(
    operation_id TEXT NOT NULL,
    step_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal > 0),
    search_target_key TEXT NOT NULL,
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 5),
    candidate_bytes INTEGER NOT NULL CHECK(candidate_bytes > 0),
    last_successful_bytes INTEGER NOT NULL CHECK(last_successful_bytes >= 0),
    attempt_json TEXT NOT NULL,
    result_json TEXT,
    updated_at_utc_ms INTEGER NOT NULL,
    PRIMARY KEY(operation_id,step_id,ordinal),
    FOREIGN KEY(operation_id,step_id) REFERENCES operation_steps(operation_id,step_id) ON DELETE CASCADE
);