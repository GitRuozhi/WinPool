# WinPool V0.43 产品瘦身计划

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

> 本文件仅为中文阅读副本；计划权威以无 `.zh-CN` 后缀的
> [Plan.md](Plan.md) 为准。

## 0. 状态、授权与基线

- **计划状态：** 范围已确认，尚未开始实施
- **创建日期：** 2026-08-31
- **基线提交：** `ac0041b01a90fe8a4a995ca6c0ab3bb1cf3eb14b`
- **工作分支：** `main`
- **当前产品版本：** V0.42
- **目标产品版本：** V0.43
- **阶段性质：** 1.0 前破坏性产品瘦身；不新增用户功能

开发者已经确认以下控制决定：

1. WinPool 应在实质降低产品与维护复杂度的前提下尽快达到 V1.0。
2. V1.0 不包含磁盘测试工作区和开发者/AI Agent 工作区；这些功能在 1.x 各版本中逐步完善，完整工作区在正式 V2.0 推出。
3. “测试”和“开发”导航标签继续存在，但页面只显示简短双语 V2.0 路线说明。
4. V0.43 完整移除活动磁盘测试实现，而不是只隐藏或禁用 UI。
5. 不要求数据库兼容。WinPool 仍处于开发期；V0.43 不迁移、导入、改写或打开 schema 13。
6. 本阶段绝不卸载或删除 WinPool 之外已有的外部工具和用户文件。

未来 1.x 完善和 V2 恢复以现有 Git 历史为权威。提交 `ea71e6b` 保留原完整测试 UI 与后端；上面的基线
提交保留改成占位页后的测试后端。V0.43 不为了充当未来归档而保留无消费者的运行时代码。

编写本计划不等于授权实施、push、tag、GitHub Release、二进制上传、部署或真实存储修改。
只有开发者明确要求执行本计划后才开始实施。

## 1. 目标与必达结果

V0.43 建立一个面向 1.x 的小型、真实基础，只聚焦存储拓扑、管理/编辑、监控、设置和数据安全。

阶段完成时必须同时满足：

1. 活动解决方案不再包含磁盘测试计划器、执行器、Worker、外部基准适配器、测试历史、预设、
   导出、Dite 导入、CopyBatch 测试、测试辅助动作或开发诊断后端。
2. 交付运行时只保留 `WinPool.App` 和 `WinPool.Agent` 两种进程角色。
3. 磁盘盘点、拓扑、模拟编辑、监控、健康事件、设置、托盘和 Agent 持久化继续工作。
4. 测试与开发页保持简单、可访问、双语路线占位页，不依赖 Agent、数据库、工具或 Worker。
5. SQLite 从干净 schema 14 开始；schema 13 及更早数据库拒绝且不迁移、不修改。
6. App 到 Agent IPC 从 protocol 4 开始，只保留 1.x 仍使用的操作。
7. 构建、便携 staging、文档、自动测试和产品版本必须描述同一个收缩后的架构。

代码行下降只作为证据，不能代替正确职责和行为。预计移除约 22,000～25,000 行产品代码和
9,000～11,000 行功能专属测试代码，但完成条件不绑定任意行数指标。

## 2. 永久安全与产品边界

- 真实磁盘、分区、卷、Storage Pool、Storage Tier 和 Virtual Disk 修改继续默认拒绝；V0.43
  不实现 V0.5 操作。
- 本阶段存储编辑仍只有模拟执行路径。
- 盘点与监控对存储结构保持只读。
- Agent 继续作为 SQLite 唯一正常写入者。
- 持久化、日志、导入、导出或复制的硬件信息继续经过既有脱敏边界。
- 固定只读盘点 PowerShell 继续嵌入程序集并通过标准输入提供；V0.43 不创建独立盘点脚本。
- 不增加自由命令、脚本、基准、插件、SDK 或公共自动化入口。
- V0.43 产品不捆绑、下载、安装、启动、修改或删除任何外部程序。
- 不因为未来版本可能需要而保留无消费者的安全或进程框架。未来 V0.5 计划必须随首个已批准
  真实管理操作一起引入其所需的最小提权边界。

## 3. 目标产品与运行架构

### 3.1 进程模型

目标运行时为：

```text
WinPool.App.exe
    WinUI 外壳、导航、页面和用户交互

Agent/WinPool.Agent.exe
    托盘、盘点、监控、SQLite 单写入、App IPC、持久化和生命周期
```

以下角色退出活动解决方案和便携交付：

- `WinPool.TestWorker.exe`；
- `WinPool.ElevatedBroker.exe`。

TestWorker 只服务延期后的测试产品。移除测试辅助和外部工具安装后，当前 Broker 操作目录没有
仍存的 1.x 消费者。保留空 Broker 会继续携带 IPC、监管、发布、恢复、审计和安全代码，却没有
当前能力，因此 V0.43 将其退役。这不允许未来真实操作直接放入 App 或 Agent；V0.5 必须在任何
真实操作出现前重新设计最小提权路径。

监控继续运行在 Agent 中。只要持续监控偏好开启，关闭主窗口后 Agent 和监控会话仍可在托盘继续。

### 3.2 保留与移除的功能边界

V0.43 保留：

- 本机只读盘点与存储拓扑；
- 不依赖磁盘测试结果的 Manage 投影、详情、导航、比较和导出；
- 持久模拟系统与模拟编辑；
- 仍被模拟和管理路径使用的操作计划、哈希、授权、策略和审计概念；
- 持续磁盘监控、存储健康事件、监控持久化、汇总、CSV 导出、托盘控制和恢复；
- 主题、强调色、语言、欢迎、启动、监控、数据位置和其他仍存设置；
- 测试与开发导航标识及其占位页。

V0.43 移除：

- 登记目录文件测试及全部测试授权/工作区类型；
- I/O、Copy、Mixed Directory 和 Dite 测试定义图；
- DiskSpd、fio、Dite、RoboCopy、RAMMap 的发现、配置、调用、安装、解析、进度和结果语义；
- TestWorker IPC、监管、进程调度、取消、暂停/恢复、事件投影和进程树终止；
- 测试历史、指标、延迟直方图、产物、预设、比较、导出、旧版导入、CopyBatch 和恢复；
- 测试专用的临时清理、缓存清理、卷 flush/optimize、临时电源计划、进程调度和工具安装；
- 当前开发诊断请求、响应、投影、算法目录、近期计划视图和对应测试；
- 当前 Broker 契约、管道、Host、可执行文件及系统支持 review/audit/recovery 路径。

## 4. 外部工具与本地文件处理

### 4.1 产品集成

完整退役 `WinPool.ToolManagement` 和 `WinPool.Testing.Tools`。设置页不再显示“外部工具”，
App/Agent/Broker 不再检测、配置、哈希、下载、安装、启动或记录外部工具。

RoboCopy 是 Windows 组件，但 WinPool 的 RoboCopy 适配器仍属于测试产品代码，因此移除适配器，
不触碰 Windows 自身的可执行文件。

### 4.2 偏好

移除 `UserPreferences.CustomToolPaths` 和 `PreferencesToolPathConfiguration`。其余偏好继续使用
`settings.json` format 1。System.Text.Json 可以忽略旧的未知 `CustomToolPaths` 字段；下一次
成功保存只写仍存模型。V0.43 不为了移除工具路径而重置主题、语言、监控、欢迎或启动偏好。

### 4.3 已有文件

V0.43 不删除或卸载：

- 用户安装的 DiskSpd、fio、Dite、RAMMap 或其他外部程序；
- Windows RoboCopy；
- 旧数据根中已有的 WinPool 受管工具载荷或下载；
- 活动数据库之外的旧测试导出、导入 CSV 或用户选择的证据。

新产品忽略这些文件。任何后续清理都必须另行明确指定。`ManagedTools` 和 `tool-downloads` 不再
是受支持活动数据类别。数据位置原子切换内部需要的临时 staging 可以保留，但不得成为外部工具
载荷权威。

## 5. 持久化重置与 schema 14

### 5.1 兼容决定

V0.43 不提供 SQLite 迁移。`WinPoolSqliteStore` 必须：

1. 在空数据库中创建 schema 14；
2. 只打开与 schema 14 完全一致的数据库；
3. schema 13 及更早版本只拒绝，不写入、不删表、不导入、不导出、不尝试修复；
4. 未来版本继续按现有方式 fail-closed 拒绝。

禁止改变结构后继续复用 schema 13 编号。

### 5.2 schema 14 保留表

schema 14 只保留以下有当前消费者的表：

- `schema_info`；
- `workspace_state`；
- `systems`；
- `inventory_snapshots`；
- `local_inventory_document`；
- `storage_objects`；
- `storage_relationships`；
- `operation_plans`；
- `operation_steps`；
- `execution_events`；
- `simulation_documents`；
- `simulation_edit_commits`；
- `monitor_sessions`；
- `monitor_devices`；
- `monitor_samples`；
- `storage_health_events`；
- `monitor_rollups`；
- `inventory_comparisons`；
- `agent_sessions`；
- `worker_processes`。

`worker_processes` 继续表达仍存 App 和真实 Inventory 子进程生命周期。移除 TestWorker、
ElevatedBroker、ExternalTool 进程类型及其全部处理。

监控中移除测试专属 `MayBeAffectedByActiveTest`。其唯一持久化编码是 `monitor_samples.sample_flags`，
由 `MonitorSampleBatchWriter` 写入、仅监控 CSV 导出读取；活动 Agent 构造 `PdhDiskMonitorSource`
时未传 `isTestActive` 回调，运行时该标记恒为 false。该列没有仍存含义，schema 14 直接移除，
不永久写入无意义的 0。

### 5.3 退役表

schema 14 不包含：

- `test_presets`；
- `system_support_audit_events`；
- `system_support_recovery`；
- `test_definitions`；
- `test_runs`；
- `test_steps`；
- `test_events`；
- `test_metrics`；
- `latency_histograms`；
- `copy_batch_manifests`；
- `copy_batches`；
- `copy_batch_entries`；
- `legacy_test_imports`；
- `legacy_test_runs`；
- `legacy_test_metrics`；
- `artifacts`；
- `algorithm_registry`；
- `external_tools`；
- `tool_install_events`。

schema verifier 和持久化测试必须断言 schema 14 精确表、列、索引、外键和约束，也必须断言退役表
不能通过任何仓储初始化重新出现。

### 5.4 开发数据重置

V0.43 原生验证从干净数据根开始。重置本机开发数据前，实施必须停止并确认精确 App/Agent 进程，
解析活动 Standard/Portable 根。已有根不删除，移动到项目父级可恢复位置：

```text
Rubbish/20260831_winpool_v043_state_reset/
```

保留相对源路径；确认源不再活动且目标存在后才生成 schema 14。应用启动时不静默移动或删除未知
用户数据根。

## 6. IPC protocol 4 与活动契约

删除请求/响应族是有意的内部破坏性变更。V0.43 将 `IpcProtocol.CurrentVersion` 从 3 升至 4，
使不匹配的 App/Agent 在握手时失败，而不是假装兼容。

protocol 4 移除：

- 所有 Start/Cancel/Pause/Resume/Get/List/Export 测试消息；
- 测试预设消息；
- Dite 导入/历史/摘要消息；
- 外部工具检测/配置/安装消息；
- 测试辅助 review/execute 消息；
- 开发诊断消息；
- TestWorker 与 ElevatedBroker 管道标识、握手、消息类型和校验器；
- 测试、外部工具、Broker 能力、事件、快照和进程投影。

protocol 4 保留启动、快照、激活主窗口、盘点、管理、工作区、模拟、监控、监控导出、偏好/生命周期、
数据位置和关闭所需的封闭、已鉴权 App-Agent 消息。

`WorkspacePage.Test` 和 `WorkspacePage.Development` 继续存在，因为导航仍显示占位页。记住其中任一
作为上次页面仍是有效行为，但不得重新引入后端依赖。

## 7. 已批准退役清单

本节按仓库删除规则准确命名退役目标。实施时，完整退役文件和目录移动到父项目 Rubbish：

```text
Rubbish/20260831_winpool_v043_test_development_retirement/Program/WinPool/
```

保留相对路径。混合文件只精确编辑；不能因为同文件包含退役契约就移动仍存代码。

### 7.1 完整产品目录

- `src/WinPool.Testing/`
- `src/WinPool.Testing.Tools/`
- `src/WinPool.ToolManagement/`
- `workers/WinPool.TestWorker/`
- `workers/WinPool.ElevatedBroker/`

### 7.2 保留项目中的完整退役产品文件

Application 与 Execution：

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

Agent：

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

持久化与 Windows 集成：

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

监控：

- `src/WinPool.Monitoring/MonitorIdleDetector.cs`（整个文件是 CopyBatch 闲置判定逻辑，唯一生产
  消费者是被退役的 `CopyBatchExecutionCoordinator.cs`）

移动任何完整文件前，必须最终检查引用、序列化、DI、源生成、XAML 和项目引用。若发现新的真实管理
或监控消费者，停止该文件退役并把仍存基础拆到真实的非测试所有者；不得因此保留整个延期子系统。

### 7.3 完整功能专属测试目录

- `tests/WinPool.Testing.Tests/`
- `tests/WinPool.Testing.Tools.Tests/`
- `tests/WinPool.TestWorker.Tests/`
- `tests/WinPool.ToolManagement.Tests/`

### 7.4 保留测试项目中的功能专属文件

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

IPC、Agent session、schema、runtime repository、storage location、architecture 和 execution policy 等
共享测试只移除退役用例。保护仍存管理、监控、安全、脱敏、进程身份、数据库所有权和 fail-closed
行为的测试必须保留。

以下混合测试文件需要精确编辑，不得整体移除：

- `tests/WinPool.Agent.Tests/AgentProcessRegistryTests.cs`（TestWorker 与 ElevatedBroker 进程
  类型用例）；
- `tests/WinPool.Agent.Client.Tests/NamedPipeAgentConnectionTests.cs`（退役请求族的端到端管道
  覆盖）；
- `tests/WinPool.Monitoring.Tests/MonitoringAlgorithmTests.cs`（仅 CopyBatch 闲置判定用例）；
- `tests/WinPool.Persistence.Tests/ExecutionAndTestRepositoryTests.cs`（系统支持审计用例；随 WP5
  的纯执行所有者一并改名）；
- `tests/WinPool.Persistence.Tests/SqliteRepositoryTests.cs`（`sample_flags` SQL 与
  `MayBeAffectedByActiveTest` 构造）。

### 7.5 必须精确编辑的混合文件

至少审计并缩减以下混合文件，不能整体删除：

- `Directory.Build.props`
- `Directory.Build.targets`
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
- `src/WinPool.Execution/OperationPlanning.cs`
- `src/WinPool.Execution/OperationPolicyEvaluator.cs`
- `src/WinPool.Execution/OperationSecurityCatalog.cs`
- `src/WinPool.Infrastructure.Sqlite/ExecutionAndTestRepositories.cs`
- `src/WinPool.Infrastructure.Sqlite/MonitorCsvExporter.cs`
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

本清单是范围边界，不授权相邻清理；无关代码保持不变。

## 8. 工作包与顺序

### WP1：替代护栏与可复现基线

1. 结构移除前执行并记录当前 Release 解决方案测试和构建。
2. 增加或修改架构测试，保护两进程 staging、纯占位页、protocol 4、schema 14 表集合、Agent
   监控所有权以及退役项目引用不存在。
3. 若移除会消灭监控、关闭、偏好、数据位置或进程身份行为的唯一证据，先补充最小保留测试。
4. 记录修改前产品/测试行数、项目数和 staging 可执行文件数。

完整源码目录移动出活动树前，必须先建立替代与回归护栏。

### WP2：移除外部工具与设置表面

1. 移除设置页外部工具区域、处理器、对话框、路径选择器、检测、下载、安装、状态和本地化键。
2. 移除 `CustomToolPaths` 和偏好型工具配置。
3. 从 Agent snapshot 和启动组合中移除工具状态。
4. 移除受管工具/工具下载数据类别，同时保留数据位置原子切换能力。
5. 确认语言切换和进入设置页不再触发任何工具工作。

设置页 code-behind 中工具代码与仍存设置处理器交织（共享文本更新、语言切换重建和构造函数接线），
属于提取式删改而非整段切除。工具字符串大多是 `SettingsPage.xaml.cs` 中的硬编码双语字面量而非
`LocalizationService` 键；两个字符串表面都要清理，且不得触碰仍存设置项。

### WP3：退役 TestWorker、测试项目和 Broker

1. 将已批准完整产品和功能测试目录移动到指定 Rubbish 恢复树。
2. 移除解决方案和项目引用，包括 `tests/WinPool.Agent.Tests/WinPool.Agent.Tests.csproj` 中对
   `WinPool.Testing` 和 `WinPool.TestWorker` 的 ProjectReference。
3. 移除 Agent 构建/发布 Worker 和 Broker 的 MSBuild Target。
4. 将 `Directory.Build.props`、`Directory.Build.targets`、构建、重置和 staging 脚本缩减为 App + Agent。
5. 移除 staging 中 TestWorker/Broker 目录与可执行文件断言。
6. 保持 App/Agent 关闭、身份、单实例、托盘和监控行为。

### WP4：移除跨层契约与运行接线

1. 从 Application、IPC、Agent.Client 和 Agent 路由移除测试、工具、开发诊断、系统支持、Worker
   和 Broker 契约。
2. 移除 Agent 中测试/Broker 协调、恢复、事件、能力、进程类型和关闭分支。
3. 移除测试专属执行 intent 和策略项，不得削弱仍存真实存储修改默认拒绝策略。
4. 从监控移除测试影响标记和回调，保留采样、持久化、事件、导出和持续监控恢复。
5. 缩减混合仓储与服务，不在伪通用接口后保留测试命名类型。
6. IPC 升为 protocol 4，并更新全部保留的握手和 codec 测试。

### WP5：建立干净 schema 14

1. 用精确保留表集合替换 schema 定义。
2. 移除退役仓储、记录、SQL、索引、外键和启动注册。
3. 完整移除测试与系统支持声明后，将 `ExecutionAndTestRepositories.cs` 中保留的操作计划/事件
   部分重命名到真实的纯执行所有者。
4. 更新 schema 校验与数据位置清单以匹配缩减模型。
5. 断言 schema 13 拒绝且文件不被修改。
6. 按可恢复移动流程重置开发数据根，验证干净 schema 14 首次启动。
7. 在 schema 14 上验证监控持久化、模拟文档、工作区状态、库存缓存和数据位置切换。

### WP6：移除功能专属测试并关闭回归

1. 对应产品表面消失后，才把完整功能测试目录和文件移动到批准的恢复树。
2. 精确编辑共享测试；不得删除无关断言来让构建通过。
3. 通过修复仍存职责解决编译和测试失败，不为退役产品增加兼容 shim。
4. 对退役命名空间、消息、表名、可执行文件、设置文案和发布路径执行残留扫描。

### WP7：文档、版本和完成记录

实施及要求的自动检查成功后：

1. 将 `Directory.Build.props` 从 V0.42 更新为 V0.43；
2. 更新 README、Product、Development、Quality 和 AGENTS.md 及其中文阅读副本，反映真实两进程、
   schema 14、protocol 4 结果，包括退役 AGENTS.md 中描述已移除文件测试与外部工具适配器产品的
   规则；
3. 在 CHANGELOG 及阅读副本记录最终结果、兼容性断点、实际测试/构建、项目/行数下降和仍未验证
   人工门；
4. 执行最终一致性和退役词扫描；
5. 未经单独授权不 tag、push、发布、上传或部署。

V0.43 是本计划完成结果，不是开始标记。要求的实施或自动检查仍失败时不得升级产品版本。

## 9. 验证与验收

### 9.1 必需自动检查

在 WinPool 根目录执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

还要把 `build/Publish-Staged.ps1` 发布到一个不存在的新 staging 目录。目标只包含固定相对位置的
App 与 Agent，不得包含 Worker、Broker、外部工具、测试产物、源码、测试或本地状态载荷。

自动证据必须确认：

- 每个剩余项目都有保留产品或回归测试职责；
- 没有项目引用 Testing、Testing.Tools、ToolManagement、TestWorker、ElevatedBroker；
- 测试/开发页 code-behind 只有初始化，XAML 没有完整工作区控件；
- schema 14 精确，schema 13 拒绝且不修改；
- protocol 4 App/Agent 握手成功，protocol 3 被拒绝；
- 监控没有测试活动输入且仍能持久化有效样本；
- 保留执行策略继续拒绝未经授权的真实存储修改；
- 便携 staging 只包含预期可执行角色。

测试数量只在实际执行后记录。被移除的测试不计为 `passed`，而是因产品功能不存在而不存在。

### 9.2 必需原生/人工实施检查

以下检查是实施完成前置条件，但不自动等于后续正式发布就绪门：

1. 从已验证干净 V0.43 数据根启动，确认正常启动和空闲使用期间只出现 App 与 Agent WinPool
   进程。
2. 确认 Manage、Edit、Monitor、Settings、Welcome 和托盘无需 Worker/Broker 文件即可打开和工作。
3. 在支持窗口宽度、100% 和一个非 100% DPI、键盘导航及当前主题/语言下，确认测试和开发页只
   显示双语 V2.0 说明。
4. 确认设置页没有外部工具区域、检测、下载、安装或路径选择活动。
5. 开启持续监控，关闭主窗口至少十分钟，确认 Agent 持续采样；重开 App 后接回同一活动会话。
6. 停止监控，确认偏好、Agent 和托盘状态对账。
7. 确认 schema 13 启动 fail-closed 且数据库文件未改变；再确认显式重置后建立 schema 14。
8. 确认没有真实存储修改、外部工具调用、UAC Broker、TestWorker 或隐藏测试文件写入。

每项人工结果按 Quality 词汇真实记录为 `passed`、`failed`、`unverified`、`not_required` 或
`deferred_by_user`。自动构建成功不能替代原生、DPI、托盘或长时间证据。

## 10. 明确不做

- 不实现任何 V0.5 真实存储管理修改。
- 不开始 V2 测试、开发、AI Agent、插件或公共自动化设计。
- 不为 V2 保留兼容适配器、休眠项目、禁用服务、数据库影子表或无用 IPC 消息。
- 不迁移 schema 13 数据或工具/测试历史。
- 不卸载或删除外部工具和用户证据。
- 除本计划所需移除外，不重做 Manage、Edit、Monitor、Settings、Welcome 或全局视觉语言。
- 不创建 MSIX、Store 材料、证书、账户、Release、tag、上传或部署。
- 不修改 `Program/WinPool` 外的 Research、Tests 证据、Dite、KS、Showcase 或冻结 Archive 历史。
- 编辑混合文件时不顺手整理无关代码。

## 11. 停止条件

出现以下情况时停止对应工作包并请求开发者决定：

- 列出的完整退役目标存在已确认的管理/监控消费者，且无法在不改变产品范围时拆出；
- 移除已知测试回调后，监控仍依赖 TestWorker、外部工具或 Broker；
- 发现当前已批准的 V1 管理操作依赖现有 Broker 契约；
- 无法解析精确活动数据库或重置目标，或仍有 WinPool 进程占用；
- schema 13 拒绝会修改旧库或在旁边静默建立部分 schema 14；
- protocol 4 移除需要 Product 中不存在的公共兼容承诺；
- 只有削弱安全策略或删除有意义安全测试，才能让真实存储默认拒绝继续通过；
- 必须整体删除共享测试才能隐藏无法解释的失败；
- 工作区出现无法安全合并的同文件用户修改；
- 自动回归无法归因并在本计划内修正。

## 12. 完成与归档门

仅当以下条件全部满足时，V0.43 实施才完成：

- WP1～WP7 完成且没有未解决停止条件；
- 活动解决方案与 staging 只包含 App 和 Agent 运行角色；
- 完整磁盘测试、外部工具、当前 Broker 和开发诊断实现已退出活动解决方案；
- schema 14 与 protocol 4 是唯一当前内部契约；
- 测试和开发页仍是可工作的简单占位页；
- 本计划要求的管理、模拟、监控、设置、托盘、启动和持久化自动检查通过；
- 必需原生检查具有真实记录；
- 当前英文文档与中文阅读副本和实施结果一致；
- V0.43 已写入 CHANGELOG 和唯一产品版本源；
- 不提交无关文件或生成产物；
- 未经单独授权，不声称 push、tag、Release、上传或部署。

实施完成不会自动开始或通过 V0.43 正式验收。实施门关闭后，由开发者决定是否进入正式测试。
阶段真正结束时，本计划冻结到 `docs/Archive/V0.43/`，之后不得为了让历史更整洁而改写。
