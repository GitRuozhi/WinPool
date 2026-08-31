# WinPool V0.43 Product Slimming Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed scope; implementation not started
- **Created:** 2026-08-31
- **Baseline commit:** `ac0041b01a90fe8a4a995ca6c0ab3bb1cf3eb14b`
- **Working branch:** `main`
- **Current product version:** V0.42
- **Target product version:** V0.43
- **Stage type:** destructive pre-1.0 product slimming; no new user feature

The developer has made the following controlling decisions:

1. WinPool must reach V1.0 as quickly as practical with materially lower
   product and maintenance complexity.
2. The complete Test workspace and the complete Development/AI Agent workspace
   are excluded from every WinPool 1.x release and are deferred to V2.0.
3. The Test and Development navigation tabs remain, but their pages contain
   only short bilingual V2.0 roadmap notices.
4. V0.43 removes the complete live disk-test implementation rather than hiding
   it behind disabled UI.
5. Database compatibility is not required. WinPool is still in development;
   schema 13 is not migrated, imported, rewritten, or opened by V0.43.
6. Existing external tools or user files outside WinPool are never uninstalled
   or deleted by this work.

The existing Git history is the recovery authority for future V2 work. Commit
`ea71e6b` retains the former complete Test UI and backend; the baseline commit
above retains the backend after the UI became a placeholder. V0.43 does not keep
dead runtime code solely as a future archive.

Writing this Plan does not authorize implementation, push, tag, GitHub Release,
binary upload, deployment, or real storage mutation. Implementation starts only
after the developer explicitly asks to execute this Plan.

## 1. Objective and required outcome

V0.43 creates a small, truthful 1.x foundation centered on storage topology,
management/editing, monitoring, settings, and data safety.

The completed stage must have all of these properties:

1. The live solution contains no disk-test planner, runner, worker, external
   benchmark adapter, test history, test preset, test export, Dite import,
   copy-batch test, test support action, or developer diagnostics backend.
2. The shipped runtime has two process roles only:
   `WinPool.App` and `WinPool.Agent`.
3. Disk inventory, topology, simulation editing, monitoring, health events,
   settings, tray behavior, and Agent-owned persistence continue to work.
4. The Test and Development pages remain simple, accessible, bilingual roadmap
   placeholders and have no Agent, database, tool, or worker dependency.
5. SQLite starts from a clean schema 14. Schema 13 and earlier databases are
   rejected without migration or modification.
6. App-to-Agent IPC starts from protocol 4 and contains only surviving 1.x
   operations.
7. The build, staged portable tree, documentation, automated tests, and product
   version all describe the same reduced architecture.

Line-count reduction is recorded as evidence, not used as a substitute for
correct ownership or behavior. The expected removal is approximately
22,000–25,000 production lines plus 10,000–11,000 feature-specific test lines,
but no completion claim depends on achieving an arbitrary number.

## 2. Permanent safety and product boundaries

- Real disk, partition, volume, Storage Pool, Storage Tier, and Virtual Disk
  mutation remains denied. V0.43 does not implement V0.5 operations.
- Simulation remains the only storage-edit execution path in this stage.
- Inventory and monitoring remain read-only with respect to storage structure.
- The Agent remains the only normal SQLite writer.
- Persisted, logged, imported, exported, or copied hardware information remains
  behind the existing redaction boundary.
- Fixed read-only inventory PowerShell remains assembly-embedded and supplied
  through standard input; V0.43 does not create a standalone inventory script.
- No free-form command, script, benchmark, plug-in, SDK, or public automation
  surface is introduced.
- No external program is bundled, downloaded, installed, launched, changed, or
  removed by the V0.43 product.
- V0.43 does not preserve an unused security or process framework merely because
  a future version might need it. A future V0.5 Plan must introduce the minimum
  elevated boundary required by its first approved real management operation.

## 3. Target product and runtime architecture

### 3.1 Process model

The target runtime is:

```text
WinPool.App.exe
    WinUI shell, navigation, pages, and user interaction

Agent/WinPool.Agent.exe
    tray runtime, inventory, monitoring, SQLite single writer,
    App IPC, persistence, and lifecycle ownership
```

The following executable roles leave the live solution and portable artifact:

- `WinPool.TestWorker.exe`;
- `WinPool.ElevatedBroker.exe`.

`WinPool.TestWorker` exists only for the deferred test product. The current
Broker operation catalog has no surviving 1.x consumer after test support and
external-tool installation are removed. Keeping an empty Broker would retain
IPC, supervision, publishing, recovery, audit, and security code without a
current capability. It is therefore retired in V0.43. This decision does not
authorize future real operations to run inside App or Agent; V0.5 must design a
new minimal elevated path before any such operation exists.

Monitoring remains inside `WinPool.Agent`. Closing the main window may leave the
Agent and an enabled continuous-monitoring session running in the tray exactly
as defined by the surviving product settings.

### 3.2 Surviving functional boundary

V0.43 retains:

- local read-only inventory and storage topology;
- Manage projections, details, navigation, comparison, and export that do not
  depend on disk-test results;
- persistent simulation systems and simulation editing;
- operation planning, hashing, authorization, policy, and audit concepts used by
  surviving simulation and management paths;
- continuous disk monitoring, storage-health events, monitor persistence,
  rollups, CSV export, tray controls, and recovery;
- theme, accent, language, welcome, startup, monitoring, data-location, and
  other surviving settings;
- the Test and Development navigation identities and their placeholder pages.

V0.43 removes:

- registered-directory file testing and all test authorization/workspace types;
- I/O, copy, mixed-directory, and Dite test definition graphs;
- DiskSpd, fio, Dite, RoboCopy, and RAMMap discovery, configuration, invocation,
  installation, parsing, progress, and result semantics;
- TestWorker IPC, supervision, process scheduling, cancellation, pause/resume,
  event projection, and process-tree termination;
- test history, metrics, latency histograms, artifacts, presets, comparison,
  export, legacy import, copy batches, and recovery;
- test-specific temporary cleanup, cache clear, volume flush/optimize, temporary
  power plan, process scheduling, and tool-install support actions;
- the current Development diagnostics request, response, projection, algorithm
  catalog, recent-plan view, and related tests;
- the current Broker contracts, pipe, host, executable, and system-support
  review/audit/recovery path.

## 4. External-tool and local-file treatment

### 4.1 Product integration

The complete `WinPool.ToolManagement` and `WinPool.Testing.Tools` product
integrations are retired. Settings no longer shows an External tools section,
and App/Agent/Broker no longer detects, configures, hashes, downloads, installs,
launches, or records external tools.

RoboCopy is a Windows component, but the WinPool RoboCopy adapter is still test
product code and is removed. The Windows executable itself is untouched.

### 4.2 Preferences

`UserPreferences.CustomToolPaths` and
`PreferencesToolPathConfiguration` are removed. `settings.json` remains format
1 for the surviving preferences. System.Text.Json may ignore the old unknown
`CustomToolPaths` member; the next successful save rewrites only the surviving
model. V0.43 does not reset theme, language, monitoring, welcome, or startup
preferences merely to remove tool paths.

### 4.3 Existing files

V0.43 does not delete or uninstall:

- user-installed DiskSpd, fio, Dite, RAMMap, or any other external program;
- Windows RoboCopy;
- old WinPool-managed tool payloads or downloads already present in a data root;
- old test exports, imported CSV files, or user-selected evidence outside the
  active WinPool database.

The new product ignores those files. Any later cleanup is a separate,
explicitly targeted operation. `ManagedTools` and `tool-downloads` cease to be
supported live data categories. Internal temporary staging required for an
atomic data-location switch may remain, but it must not become an external-tool
payload authority.

## 5. Persistence reset and schema 14

### 5.1 Compatibility decision

V0.43 has no SQLite migration path. `WinPoolSqliteStore` must:

1. create schema 14 in an empty database;
2. open only a database that exactly matches schema 14;
3. reject schema 13 and earlier without writing, dropping, importing, exporting,
   or attempting best-effort repair;
4. reject a future schema version in the existing fail-closed manner.

Reusing schema number 13 with different tables is forbidden.

### 5.2 Schema 14 retained tables

Schema 14 contains only the current consumers of these tables:

- `schema_info`;
- `workspace_state`;
- `systems`;
- `inventory_snapshots`;
- `local_inventory_document`;
- `storage_objects`;
- `storage_relationships`;
- `operation_plans`;
- `operation_steps`;
- `execution_events`;
- `simulation_documents`;
- `simulation_edit_commits`;
- `monitor_sessions`;
- `monitor_devices`;
- `monitor_samples`;
- `storage_health_events`;
- `monitor_rollups`;
- `inventory_comparisons`;
- `agent_sessions`;
- `worker_processes`.

`worker_processes` remains because it still represents surviving App and any
actual inventory child lifecycle. TestWorker, ElevatedBroker, and ExternalTool
process kinds and all handling for them are removed.

The test-only `MayBeAffectedByActiveTest` property is removed from monitoring.
If `monitor_samples.sample_flags` has no independently verified surviving
meaning, the column is removed from schema 14 rather than permanently writing
zero into dead state.

### 5.3 Retired tables

Schema 14 does not contain:

- `test_presets`;
- `system_support_audit_events`;
- `system_support_recovery`;
- `test_definitions`;
- `test_runs`;
- `test_steps`;
- `test_events`;
- `test_metrics`;
- `latency_histograms`;
- `copy_batch_manifests`;
- `copy_batches`;
- `copy_batch_entries`;
- `legacy_test_imports`;
- `legacy_test_runs`;
- `legacy_test_metrics`;
- `artifacts`;
- `algorithm_registry`;
- `external_tools`;
- `tool_install_events`.

The schema verifier and persistence tests must assert the exact schema 14 table,
column, index, foreign-key, and constraint set. They must also assert that the
retired tables cannot reappear through repository initialization.

### 5.4 Development data reset

Native V0.43 verification starts from a clean data root. Before resetting local
development data, implementation must stop and verify the exact App and Agent
processes and resolve the active Standard/Portable roots. Existing roots are
moved, not deleted, to the parent-project recovery location:

```text
Rubbish/20260831_winpool_v043_state_reset/
```

Relative source paths are preserved. The source must no longer be active and the
destination must exist before schema 14 is generated. The application itself
does not silently move or erase an unknown user data root during startup.

## 6. IPC protocol 4 and live contracts

Removing request and response families is an intentional breaking internal
change. V0.43 increments `IpcProtocol.CurrentVersion` from 3 to 4 so mismatched
App and Agent binaries fail the handshake instead of pretending compatibility.

Protocol 4 removes:

- all Start/Cancel/Pause/Resume/Get/List/Export test messages;
- test preset messages;
- Dite import/history/summary messages;
- external-tool detect/configure/install messages;
- test support review/execute messages;
- Development diagnostics messages;
- TestWorker and ElevatedBroker pipe identities, handshakes, message types, and
  validators;
- test, external-tool, and elevated-broker capabilities, events, snapshots, and
  process projections.

Protocol 4 retains the closed authenticated App-to-Agent messages required by
startup, snapshot, main-window activation, inventory, management, workspace,
simulation, monitoring, monitor export, preferences/lifecycle, data-location,
and shutdown behavior.

`WorkspacePage.Test` and `WorkspacePage.Development` remain because navigation
still exposes the placeholders. Remembering either as the last page remains
valid and must not revive a backend dependency.

## 7. Approved retirement inventory

This section names the obsolete file targets required by the repository deletion
policy. During implementation, complete retired files and directories are moved
to the parent-project Rubbish tree under:

```text
Rubbish/20260831_winpool_v043_test_development_retirement/Program/WinPool/
```

with relative paths preserved. Mixed files are edited precisely; surviving code
must not be moved merely because it shares a file with a retired contract.

### 7.1 Complete product directories

- `src/WinPool.Testing/`
- `src/WinPool.Testing.Tools/`
- `src/WinPool.ToolManagement/`
- `workers/WinPool.TestWorker/`
- `workers/WinPool.ElevatedBroker/`

### 7.2 Complete product files in surviving projects

Application and Execution:

- `src/WinPool.Application/CopyBatchContracts.cs`
- `src/WinPool.Application/DiteFileGenerationBounds.cs`
- `src/WinPool.Application/ElevatedBrokerContracts.cs`
- `src/WinPool.Application/ExternalToolContracts.cs`
- `src/WinPool.Application/SystemTestSupportExecution.cs`
- `src/WinPool.Application/TestingContracts.cs`
- `src/WinPool.Application/TestPresetContracts.cs`
- `src/WinPool.Application/TestRunReconciliation.cs`
- `src/WinPool.Application/TestWorkerContracts.cs`
- `src/WinPool.Application/ToolProcessExitPolicy.cs`
- `src/WinPool.Execution/AuthorizedTestWorkspace.cs`

Agent:

- `src/WinPool.Agent/AgentSystemSupportCoordinator.cs`
- `src/WinPool.Agent/AgentTestCoordinator.cs`
- `src/WinPool.Agent/AgentTestRunWorkflow.cs`
- `src/WinPool.Agent/ChildLifecycleCallbacks.cs`
- `src/WinPool.Agent/CopyBatchExecutionCoordinator.cs`
- `src/WinPool.Agent/CopyBatchRecoveryCoordinator.cs`
- `src/WinPool.Agent/DevelopmentDiagnosticsProjection.cs`
- `src/WinPool.Agent/ElevatedBrokerProcessHost.cs`
- `src/WinPool.Agent/LocalTestStepExecutor.cs`
- `src/WinPool.Agent/PreparedExecutionStep.cs`
- `src/WinPool.Agent/SystemSupportRecoveryCoordinator.cs`
- `src/WinPool.Agent/SystemSupportReviewStore.cs`
- `src/WinPool.Agent/SupervisedProcessExitPolicy.cs`
- `src/WinPool.Agent/TestExecutionRules.cs`
- `src/WinPool.Agent/TestPowerPlanScope.cs`
- `src/WinPool.Agent/TestProcessSchedulingScope.cs`
- `src/WinPool.Agent/TestRunStartCoordinator.cs`
- `src/WinPool.Agent/TestWorkerAgentEventProjector.cs`
- `src/WinPool.Agent/TestWorkerProcessHost.cs`
- `src/WinPool.Agent/TestWorkerSupervisor.cs`

Persistence and Windows integration:

- `src/WinPool.Infrastructure.Sqlite/CopyBatchRepository.cs`
- `src/WinPool.Infrastructure.Sqlite/DiteLegacyImportRepository.cs`
- `src/WinPool.Infrastructure.Sqlite/SystemSupportRecoveryRepository.cs`
- `src/WinPool.Infrastructure.Sqlite/TestArtifactStore.cs`
- `src/WinPool.Infrastructure.Sqlite/TestRunExporter.cs`
- `src/WinPool.Infrastructure.Sqlite/TestToolResultRepositoryWriter.cs`
- `src/WinPool.Infrastructure.Sqlite/UserTestPresetRepository.cs`
- `src/WinPool.Infrastructure.Windows/WindowsMsiToolInstallPort.cs`
- `src/WinPool.Infrastructure.Windows/WindowsSystemSupportPorts.cs`
- `src/WinPool.Infrastructure.Windows/WindowsToolVersionProbe.cs`

Before moving any listed complete file, implementation performs a final reference,
serialization, DI, source-generation, XAML, and project-reference scan. If a
listed file contains a newly discovered surviving management or monitoring
consumer, stop that file's retirement and split the surviving primitive under a
truthful non-test owner; do not retain the whole deferred subsystem.

### 7.3 Complete feature-specific test directories

- `tests/WinPool.Testing.Tests/`
- `tests/WinPool.Testing.Tools.Tests/`
- `tests/WinPool.TestWorker.Tests/`
- `tests/WinPool.ToolManagement.Tests/`

### 7.4 Feature-specific files in surviving test projects

- `tests/WinPool.Agent.Tests/AgentTestCoordinatorTests.cs`
- `tests/WinPool.Agent.Tests/DevelopmentDiagnosticsProjectionTests.cs`
- `tests/WinPool.Agent.Tests/LocalTestStepExecutorTests.cs`
- `tests/WinPool.Agent.Tests/SystemSupportRecoveryCoordinatorTests.cs`
- `tests/WinPool.Agent.Tests/SystemSupportReviewStoreTests.cs`
- `tests/WinPool.Agent.Tests/TestPowerPlanScopeTests.cs`
- `tests/WinPool.Agent.Tests/TestProcessSchedulingScopeTests.cs`
- `tests/WinPool.Agent.Tests/TestStepOrderingTests.cs`
- `tests/WinPool.Agent.Tests/TestSupportActionValidationTests.cs`
- `tests/WinPool.Agent.Tests/TestWorkerAgentEventProjectorTests.cs`
- `tests/WinPool.Agent.Tests/TestWorkerProcessHostTests.cs`
- `tests/WinPool.Application.Tests/TestRunReconciliationTests.cs`
- `tests/WinPool.Execution.Tests/AuthorizedTestWorkspaceTests.cs`
- `tests/WinPool.Infrastructure.Tests/PreferencesToolPathConfigurationTests.cs`
- `tests/WinPool.Infrastructure.Tests/WindowsPowerPlanCatalogTests.cs`
- `tests/WinPool.Infrastructure.Tests/WindowsSystemSupportPortTests.cs`
- `tests/WinPool.Persistence.Tests/CopyBatchRepositoryTests.cs`
- `tests/WinPool.Persistence.Tests/DiteLegacyImportRepositoryTests.cs`
- `tests/WinPool.Persistence.Tests/SystemSupportRecoveryRepositoryTests.cs`
- `tests/WinPool.Persistence.Tests/TestArtifactStoreTests.cs`
- `tests/WinPool.Persistence.Tests/UserTestPresetRepositoryTests.cs`

Shared tests such as IPC, Agent session, schema, runtime repository, storage
location, architecture, and execution-policy tests are edited to remove only the
retired cases. Tests protecting surviving management, monitoring, security,
redaction, process identity, database ownership, and fail-closed behavior remain.

### 7.5 Mixed files requiring precise edits

At minimum, the following mixed files must be audited and reduced rather than
deleted wholesale:

- `Directory.Build.props`
- `WinPool.slnx`
- `build/Rebuild-WinPool.ps1`
- `build/Publish-Staged.ps1`
- `build/Reset-WinPoolLocalData.ps1`
- `src/WinPool.App/SettingsPage.xaml`
- `src/WinPool.App/SettingsPage.xaml.cs`
- `src/WinPool.App/Services/LocalizationService.cs`
- `src/WinPool.App/WinPool.App.csproj`
- `src/WinPool.Application/ApplicationStartupOptions.cs`
- `src/WinPool.Application/DataRootLayout.cs`
- `src/WinPool.Application/MonitoringContracts.cs`
- `src/WinPool.Application/ProcessCoordinationContracts.cs`
- `src/WinPool.Application/Properties/AssemblyInfo.cs`
- `src/WinPool.Application/Queries.cs`
- `src/WinPool.Application/TaskEvents.cs`
- `src/WinPool.Domain/Preferences.cs`
- `src/WinPool.Execution/ExecutionModels.cs`
- `src/WinPool.Execution/OperationPolicyEvaluator.cs`
- `src/WinPool.Execution/OperationSecurityCatalog.cs`
- `src/WinPool.Infrastructure.Sqlite/ExecutionAndTestRepositories.cs`
- `src/WinPool.Infrastructure.Sqlite/MonitorRepositories.cs`
- `src/WinPool.Infrastructure.Sqlite/MonitorSampleBatchWriter.cs`
- `src/WinPool.Infrastructure.Sqlite/RuntimeRepositories.cs`
- `src/WinPool.Infrastructure.Sqlite/StorageLocationManager.cs`
- `src/WinPool.Infrastructure.Sqlite/WinPoolSqliteStore.cs`
- `src/WinPool.Infrastructure.Windows/PdhDiskMonitorSource.cs`
- `src/WinPool.Infrastructure.Windows/WindowsServices.cs`
- `src/WinPool.Infrastructure.Windows/WinPool.Infrastructure.Windows.csproj`
- `src/WinPool.Ipc/AgentControlMessageTypes.cs`
- `src/WinPool.Ipc/IpcProtocol.cs`
- `src/WinPool.Agent/AgentControlServer.cs`
- `src/WinPool.Agent/AgentProcessProjection.cs`
- `src/WinPool.Agent/AgentProcessRegistry.cs`
- `src/WinPool.Agent/AgentSessionCoordinator.cs`
- `src/WinPool.Agent/AgentShutdownWorkflow.cs`
- `src/WinPool.Agent/DesktopAgentRuntime.cs`
- `src/WinPool.Agent/Program.cs`
- `src/WinPool.Agent/TrayApplicationContext.cs`
- `src/WinPool.Agent/WinPool.Agent.csproj`
- `src/WinPool.Agent.Client/NamedPipeAgentConnection.cs`

This inventory is a scope boundary, not permission for adjacent cleanup.
Unrelated code remains untouched.

## 8. Work packages and order

### WP1: Replacement guards and reproducible baseline

1. Run and record the current Release solution tests and build before structural
   removal.
2. Add or revise architecture tests that protect the target two-process tree,
   placeholder-only pages, protocol 4 surface, schema 14 table set, Agent-only
   monitoring ownership, and absence of retired project references.
3. Add focused surviving-behavior tests where removal would otherwise erase the
   only evidence for monitoring, shutdown, preferences, storage-location, or
   process identity behavior.
4. Record current product/test line counts, project count, and staged executable
   count for before/after evidence.

Replacement and regression guards must exist before complete source directories
are moved out of the live repository tree.

### WP2: Remove external-tool and settings surface

1. Remove the Settings External tools section, handlers, dialogs, path picker,
   detection, download, install, status, and localization entries.
2. Remove `CustomToolPaths` and the preference-backed tool configuration.
3. Remove tool state from Agent snapshots and startup composition.
4. Remove managed-tool/tool-download data categories while preserving generic
   data-location atomicity.
5. Confirm language switching and Settings entry no longer trigger tool work.

### WP3: Retire TestWorker, testing projects, and Broker

1. Move the approved complete product and feature-test directories to the named
   Rubbish recovery tree.
2. Remove their solution and project references.
3. Remove Agent MSBuild targets that build or publish Worker and Broker children.
4. Reduce `Directory.Build.props`, build, reset, and staging scripts to App and
   Agent.
5. Remove staged TestWorker/Broker directories and executable assertions.
6. Preserve App/Agent process shutdown, identity, single-instance, tray, and
   monitoring behavior.

### WP4: Remove cross-layer contracts and runtime wiring

1. Remove test, tool, developer-diagnostics, system-support, Worker, and Broker
   contracts from Application, IPC, Agent.Client, and Agent routing.
2. Remove test/Broker coordination, recovery, event, capability, process-kind,
   and shutdown branches from Agent.
3. Remove test-only execution intents and policy entries without weakening the
   surviving deny-by-default storage mutation policy.
4. Remove test influence flags and callbacks from monitoring while preserving
   sampling, persistence, events, export, and continuous-monitoring recovery.
5. Thin mixed repositories and services so no test-named type remains behind an
   apparently generic interface.
6. Set IPC protocol to 4 and update all remaining handshake and codec tests.

### WP5: Establish clean schema 14

1. Replace the schema definition with the exact retained table set.
2. Remove retired repositories, records, SQL, indexes, foreign keys, and startup
   registrations.
3. Rename the surviving operation-plan/event portion of
   `ExecutionAndTestRepositories.cs` to a truthful execution-only owner after
   all test and system-support declarations are gone.
4. Update schema verification and storage-location manifests to the reduced data
   model.
5. Assert schema 13 rejection with no file modification.
6. Reset the development data root through the recoverable move procedure and
   verify a clean schema 14 first launch.
7. Verify monitoring persistence, simulation documents, workspace state,
   inventory cache, and data-location switching against schema 14.

### WP6: Remove feature-specific tests and close regressions

1. Move complete feature-test directories and files to the approved recovery
   tree only after their corresponding production surface is gone.
2. Precisely edit shared suites; do not remove unrelated assertions to make the
   build pass.
3. Resolve compiler and test failures by correcting surviving ownership, not by
   adding compatibility shims for the retired product.
4. Run residual scans for retired namespaces, message types, table names,
   executable names, settings labels, and publish paths.

### WP7: Documentation, version, and completion record

After implementation and its required automatic checks succeed:

1. update `Directory.Build.props` from V0.42 to V0.43;
2. update README, Product, Development, and Quality plus their Chinese reading
   copies to the actual two-process/schema-14/protocol-4 result;
3. record final results, compatibility break, actual tests, build, project/line
   reduction, and remaining unverified manual gates in CHANGELOG and its reading
   copy;
4. run final consistency and retired-term scans;
5. do not tag, push, publish, upload, or deploy without separate authorization.

Version V0.43 is the completed result of this Plan, not the start marker. Do not
bump the product version while required implementation or automatic checks still
fail.

## 9. Verification and acceptance

### 9.1 Required automatic checks

From the WinPool repository root:

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

The stage also runs `build/Publish-Staged.ps1` into a new nonexistent staging
directory. The staged tree must contain App and Agent in their fixed relative
locations and must not contain Worker, Broker, external-tool, test artifact,
source, test, or local-state payloads.

Automatic evidence must confirm:

- every remaining project is reachable from a surviving product or surviving
  regression-test responsibility;
- no project references Testing, Testing.Tools, ToolManagement, TestWorker, or
  ElevatedBroker;
- Test and Development page code-behind contains initialization only and the
  views contain no full workspace controls;
- schema 14 is exact and schema 13 is rejected without modification;
- protocol 4 App/Agent handshakes succeed and protocol 3 is rejected;
- monitoring has no test-active input and continues to persist valid samples;
- surviving execution policy still denies unauthorized real storage mutation;
- the portable stage contains exactly the intended executable roles.

Test counts are recorded only after execution. Removed tests do not count as
`passed`; they are absent because their product feature is absent.

### 9.2 Required native/manual implementation checks

These checks are required before calling implementation complete, but they do
not automatically constitute the later formal release-readiness gate:

1. Start from the verified clean V0.43 data root. Confirm only App and Agent
   WinPool processes appear during normal startup and idle use.
2. Confirm Manage, Edit, Monitor, Settings, Welcome, and tray entry continue to
   open and operate without Worker/Broker files.
3. Confirm Test and Development tabs show only their bilingual V2.0 notices at
   supported window widths, 100% and one non-100% DPI, keyboard navigation, and
   the current theme/language choices.
4. Confirm Settings has no External tools area, detection, download, install, or
   path-selection activity.
5. Enable continuous monitoring, close the main window, observe continued Agent
   sampling for at least ten minutes, reopen the App, and confirm it reattaches
   to the same active session.
6. Stop monitoring and confirm the preference and Agent/tray state reconcile.
7. Confirm schema 13 startup fails closed without changing the database file;
   then confirm the explicit reset path creates schema 14.
8. Confirm no real storage mutation, external tool invocation, UAC Broker launch,
   TestWorker launch, or hidden test file write occurs.

Each manual result is recorded truthfully as `passed`, `failed`, `unverified`,
`not_required`, or `deferred_by_user` under Quality vocabulary. Automatic build
success cannot replace native, DPI, tray, or long-running evidence.

## 10. Explicit non-goals

- Do not implement any V0.5 real storage-management mutation.
- Do not begin the V2 Test, Development, AI Agent, plug-in, or public automation
  design.
- Do not keep a compatibility adapter, dormant project, disabled service,
  database shadow table, or unused IPC message for V2.
- Do not migrate schema 13 data or tool/test history.
- Do not uninstall or delete external tools or user evidence.
- Do not redesign Manage, Edit, Monitor, Settings, Welcome, or global visual
  language beyond the removals required by this Plan.
- Do not create MSIX, Store material, certificates, accounts, releases, tags,
  uploads, or deployments.
- Do not modify Research, Tests evidence outside `Program/WinPool`, Dite, KS,
  Showcase, or frozen Archive history.
- Do not reorganize unrelated code while touching mixed files.

## 11. Stop conditions

Stop the affected work package and request a developer decision if:

- a listed complete retirement target has a confirmed surviving management or
  monitoring consumer that cannot be separated without changing product scope;
- monitoring requires TestWorker, external tools, or Broker behavior after the
  known test callbacks are removed;
- a currently approved V1 management operation is discovered that requires the
  existing Broker contract;
- the exact active database or reset destination cannot be resolved, or a
  WinPool process still holds it;
- schema 13 rejection modifies the old database or silently creates a partial
  schema 14 beside it;
- protocol 4 removal would require a public compatibility promise not present in
  Product;
- surviving deny-by-default storage safety can pass only by weakening policy or
  deleting a meaningful security test;
- a shared test must be removed wholesale merely to hide an unexplained failure;
- the worktree gains overlapping user changes that cannot be merged safely;
- an automatic regression cannot be attributed and corrected inside this Plan.

## 12. Completion and archival gate

V0.43 implementation is complete only when:

- WP1–WP7 have completed without an unresolved stop condition;
- the live solution and stage contain only App and Agent runtime roles;
- the complete disk-test, external-tool, current Broker, and developer-diagnostics
  implementations are absent from the live solution;
- schema 14 and protocol 4 are the only current internal contracts;
- Test and Development remain simple working placeholders;
- management, simulation, monitoring, settings, tray, startup, and persistence
  required by this Plan pass their automatic checks;
- required native checks have truthful recorded outcomes;
- current English documents and Chinese reading copies match the implemented
  result;
- V0.43 is recorded in CHANGELOG and the product version source;
- no unrelated files or generated artifacts are committed;
- no push, tag, Release, upload, or deployment is claimed without separate
  authorization.

Implementation completion does not automatically start or pass formal V0.43
acceptance. After the implementation gate, the developer decides whether to
enter formal testing. When the stage is genuinely finished, this Plan is frozen
under `docs/Archive/V0.43/`; it is never rewritten afterward to make history
appear cleaner.
