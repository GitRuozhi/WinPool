# WinPool V0.4 前架构治理计划

## 0. 状态、授权与基线

- **计划状态**：completed / frozen for archive on 2026-08-14
- **创建日期**：2026-08-14
- **基线提交**：`e9f799f5c70b109aea9549b7a7299da7b13e557e`
- **工作分支**：`refactor/v039-architecture-hardening`
- **当前产品版本**：V0.39
- **目标产品版本**：仍为 V0.39
- **阶段编号**：`V039-ARCH-HARDENING`，仅用于工作识别，不是产品版本
- **阶段性质**：进入 V0.40 前的限界架构治理；不新增产品功能

用户已授权本地 `main` 吸收最终 V0.39 提交、建立治理分支、编写本计划、将当前
Plan 提交并推送到 GitHub，以及完成本计划的全部 WP1–WP6 工作。tag、GitHub Release、
二进制上传和部署仍需另行明确授权。

本阶段保持 V0.39，不创建 `V0.39a`、`V0.399`、`V0.3.10` 或其他产品版本。
治理完成并归档后，下一正常产品计划才推进到 V0.40。

当前工作区存在与本计划无关的未跟踪文件
`docs/Reference/轻松交流.txt`；实施、验证和提交不得修改、移动或纳入该文件，
除非用户另行明确要求。

## 1. 目标

在 V0.4 视觉和基础交互开发开始前，消除最可能继续堆积的结构性风险，同时保留
V0.39 的产品行为、安全边界和内部兼容性。

完成后应达到：

1. 高置信度、无实际消费者的预留契约和无用实现已得到逐项确认并移出生产代码。
2. `DesktopAgentRuntime` 不再直接承载测试生命周期、Worker 监管、CopyBatch 执行和
   恢复协调的完整实现。
3. `TestPage` 不再同时承担测试定义、运行协调、历史、Dite 和系统支持的完整业务逻辑。
4. `SettingsPage` 不再直接实现数据位置迁移和外部工具安装的完整状态流程。
5. 架构测试继续保护真实边界，但不依赖易变的文件文本和实现细节冒充架构保证。
6. 当前文档、测试数量和 Git/GitHub 状态记录一致，不把未执行的人工门写成通过。
7. V0.40 可以在清晰职责上开展视觉和交互工作，而不继续向三个热点类增加职责。

本计划追求“停止结构恶化并建立可维护边界”，不追求一次性完美架构或全面重写。

## 2. 永久安全与兼容边界

- 保留 App、Agent、TestWorker、ElevatedBroker 四进程模型。
- 保留 Agent 的 SQLite 单写者所有权和既有 schema 12。
- 保留 typed named-pipe IPC 和 protocol 3；不得改变线上消息 schema 或兼容语义。
- 保留 TestWorker 隔离、Broker 一次性最小权限和 deny-by-default 执行。
- 不实现或开放任何真实磁盘、分区、卷、Storage Pool、Storage Tier 或 Virtual Disk
  mutation。
- 不改变 Simulation-first、精确目标验证、审计、数据脱敏和注册测试目录边界。
- DiskSpd、fio、Dite、RoboCopy 和 RAMMap 继续作为外部安装工具，通过 typed adapter
  使用；不得捆绑、重实现或增加自由命令入口。
- 不冻结公共 SDK、插件 API、数据库合同或新的跨语言协议。
- 不把超时解释为副作用命令确定失败或确定未执行。
- 不修改冻结归档来改写历史；历史错误只能在当前权威文档中追加勘误或说明。

## 3. 工作包

### WP1：事实基线和治理护栏

1. 修正 `README.md` 和 `README.zh-CN.md` 中已经过时的 V0.39 状态、授权和“最终修正
   尚未实施”描述。
2. 在当前 `CHANGELOG` 中记录测试计数勘误：按当前解决方案可复现的 Release 测试为
   526 passed、0 failed、0 skipped；不得修改冻结 Plan 中的历史原文来伪装它从未出错。
3. 核对 V0.39 GitHub Release 二进制资产的授权证据。若没有可引用的明确授权，只记录
   “授权证据未确认”的事实，不删除、不替换、不重新上传远端资产。
4. 建立治理前自动基线和热点特征测试，覆盖后续搬迁会触及的状态转换、失败恢复、
   TestWorker 监管、CopyBatch 恢复、测试定义生成、数据位置切换和工具安装边界。
5. 记录每个工作包开始前后的文件、类型、依赖和测试变化，避免用行数下降代替职责改善。

### WP2：高置信度无用代码审计与清理

以下仅是必须审计的候选，不是已经批准删除的结论：

- `src/WinPool.App/Services/DiskDetailFormatter.cs`。
- `src/WinPool.Application/Commands.cs` 中的 `IApplicationCommand`、
  `IApplicationCommandHandler` 和九个无处理器命令。
- `IOperationAuthorizationCoordinator`、`IMonitoringQuery`、`IStorageSystemQuery`、
  `IWorkspaceQuery`、`IApplicationTaskEventSink`、`IApplicationTaskEventQuery`、
  `ITestRunner`。
- `ProcessCoordinationContracts.cs` 中旧的 `WorkerRequest`、`TestWorkerRequest`、
  `InventoryWorkerRequest`、`BrokerAction`、`BrokerSystemSupportAction`、
  `BrokerToolInstallAction`、`ElevatedBrokerRequest`、`WorkerHandle` 和
  `IProcessSupervisor` 体系。
- `SystemMonotonicClock`、`MonotonicSampleClock` 和 `NormalizedSampleTime` 的生产归属；
  若它们只服务测试，应评估移动到测试项目，而不是自动删除。
- `MainWindow.xaml` 中已经退役的 `xmlns:core="using:WinPool.Core"`。

每个候选必须先检查：

1. C#、XAML、DI 注册、反射、序列化、源生成和测试引用。
2. IPC 类型判别、持久化 JSON、SQLite 历史数据和兼容性影响。
3. 是否已有真实实现或近期已批准消费者；“未来可能使用”本身不构成保留理由。
4. 移除后是否仍有其他类型表达同一业务事实，是否会迫使调用方绕过安全边界。

确认无用的完整文件不得直接删除，应移动到项目根级
`Rubbish/20260814_winpool_architecture_hardening/Program/WinPool/` 下并保留相对路径。
混合文件中的无用声明可以在确认后精确移除；不得顺手清理未列入审计记录的相邻代码。
每个候选族作为独立变更验证，不能一次性大批删除后再修编译错误。

#### WP2 清理前评价记录（2026-08-14）

统一评价方法：对 `src`、`workers`、`tests` 进行精确符号引用扫描；对项目根级其余代码和
配置进行外部引用扫描；检查 closed Agent IPC decoder、JSON/源生成特性、DI/反射入口和
SQLite/持久化路径；并以本计划 WP1 的 526 项自动测试基线作为回归起点。以下结论均未实施，
每项都等待用户逐项批准。

1. **`DiskDetailFormatter.cs`**
   - 证据：`DiskDetailFormatter` 只出现在其自身声明文件，未在 XAML、C#、测试、配置或
     项目外代码中出现；没有 DI、JSON 或 IPC 接线。
   - 风险：低。它只是未调用的展示字符串格式器，不持有数据、文件或进程副作用。
   - 建议：将完整文件移动到规定的根级 Rubbish 路径；不需要替代实现。
   - 决定：**待用户批准清理点 A**。

2. **`Commands.cs` 的预留命令总线**
   - 范围：`IApplicationCommand`、`IApplicationCommandHandler` 和
     `RefreshInventoryCommand`、`SaveWorkspaceCommand`、`RequestOperationPlanCommand`、
     `StartTestCommand`、`CancelTestCommand`、`StartMonitoringCommand`、
     `StopMonitoringCommand`、`DetectExternalToolCommand`、`PlanExternalToolInstallCommand`。
   - 证据：每个符号只在 `Commands.cs` 内出现；当前调用链使用 closed `AgentRequest`、
     专用协调器和 typed contract，而不是这套通用命令。
   - 风险：低。无 JSON discriminator、IPC message type、SQLite 表或外部项目引用；项目也
     不承诺公共 SDK/插件命令 API。
   - 建议：将完整 `Commands.cs` 移到 Rubbish，避免未来为无消费者命令建立处理器。
   - 决定：**待用户批准清理点 B**。

3. **`ExecutionContracts.cs` 的重复 Application 抽象**
   - 范围：`AuthorizationReceipt`、`AuthorizedOperation`、`IOperationPlanner`、
     `IOperationPolicyEvaluator`、`IOperationAuthorizationCoordinator` 和
     `IOperationExecutor`。
   - 证据：这些 Application 符号均只在该文件内出现。现有执行实现使用
     `WinPool.Execution` 命名空间内的独立 `AuthorizedOperation`、planner、policy 和
     executor；两者没有接线。
   - 风险：中低。名称相近，清理前必须保持 `WinPool.Execution` 版本不变，并确认编译后
     没有因 `using WinPool.Application` 隐藏的歧义；未发现序列化或外部消费者。
   - 建议：将完整 `ExecutionContracts.cs` 移到 Rubbish；这是去除重复抽象，不改变执行
     安全模型。
   - 决定：**待用户批准清理点 C**。

4. **`IMonitoringQuery`**
   - 证据：接口只在 `MonitoringContracts.cs` 声明，无实现、调用、DI、IPC 或测试替身；
     `IMonitoringCoordinator` 和监控模型仍有实际消费者。
   - 风险：低。只删除未接线的未来查询面，不触及监控采样、会话或持久化。
   - 建议：仅移除该接口声明，保留同文件其余有效契约。
   - 决定：**待用户批准清理点 D**。

5. **`Queries.cs` 的未消费者子图**
   - 范围：`StorageSystemView`、`StorageChangeKind`、`StorageChange`、
     `ObjectComparisonValue`、`ObjectComparisonRow`、`IStorageSystemQuery`、
     `IWorkspaceQuery`。
   - 证据：上述符号只在 `Queries.cs` 内出现。不得移除
     `StorageSystemSourceKind`、`IdentityStability`、`StorageObjectView`、`WorkspacePage` 或
     `WorkspaceState`：它们被 Manage、Inventory、App、Infrastructure 和测试实际使用。
   - 风险：中低。需要精确删除该子图而不是删除整个文件；保留的共享基础类型不能重命名或
     移位为不必要的重构。
   - 建议：精确移除未消费者子图，保留仍被使用的类型及其命名空间位置。
   - 决定：**待用户批准清理点 E**。

6. **`IApplicationTaskEventSink` 与 `IApplicationTaskEventQuery`**
   - 证据：两个接口只在 `TaskEvents.cs` 声明；`ApplicationTaskEvent` 本身由 Agent、Worker
     和测试使用，不能移除。
   - 风险：低。移除不会改变现有任务事件封装、Agent 事件投影或 IPC。
   - 建议：仅移除这两个未接线接口。
   - 决定：**待用户批准清理点 F**。

7. **`ITestRunner`**
   - 证据：接口只在 `TestingContracts.cs` 声明；`ITestPlanner` 和
     `ITestRunAuthorizationCoordinator` 有明确实现与消费者，必须保留。
   - 风险：低。当前运行由 Agent/TestWorker 路径承担，不使用这个抽象。
   - 建议：仅移除 `ITestRunner`，不重组相邻测试合同。
   - 决定：**待用户批准清理点 G**。

8. **旧通用 Worker/Broker 请求层**
   - 范围：`WorkerRequest`、`TestWorkerRequest`、`InventoryWorkerRequest`、`BrokerAction`、
     `BrokerSystemSupportAction`、`BrokerToolInstallAction`、`ElevatedBrokerRequest`、
     `WorkerHandle` 和 `IProcessSupervisor`。
   - 证据：这些符号只在 `ProcessCoordinationContracts.cs` 内自引用。实际进程追踪使用
     `WorkerKind`、`ProcessRegistration`、Agent 的专用 hosts/coordinators 和 closed IPC；
     后三者有生产和测试消费者，必须保留。
   - 风险：中低。需要精确删除该旧层，不能删除 `WorkerKind`、`ProcessRegistration`、
     `ProcessInstanceId`、`ShutdownReason` 或 `ShutdownResult`。
   - 建议：精确移除旧通用层，防止它与当前专用进程所有权并存并诱导错误抽象。
   - 决定：**待用户批准清理点 H**。

9. **生产项目中的测试专用单调时钟**
   - 范围：`IMonotonicClock`、`SystemMonotonicClock`、`MonotonicSampleClock` 和
     `NormalizedSampleTime`。
   - 证据：无生产调用；`MonotonicSampleClock`、`IMonotonicClock` 及其返回类型仅由
     `WinPool.Monitoring.Tests` 的算法测试使用。它们不属于死代码，但当前放在生产程序集
     中扩大了公开面。
   - 风险：低。应保持同一算法测试，改为将完整实现移至测试项目的内部 helper；不能简单
     丢弃，因为它覆盖单调时间与 UTC 回退语义。
   - 建议：移动而非删除；移动后运行 Monitoring 测试和完整自动门。
   - 决定：**待用户批准清理点 I**。

10. **`MainWindow.xaml` 的退役 `WinPool.Core` 命名空间**
    - 证据：只存在 `xmlns:core="using:WinPool.Core"` 声明，没有任何 `core:` XAML 用法；
      其余出现仅是架构测试中的退役名称文本。
    - 风险：低。删除该行不改变控件、绑定、运行时类型或 IPC；当前架构测试未扫描 XAML，
      这是它漏报残留的证据。
    - 建议：移除该单行，并在 WP6 增加对 C# 与 XAML 的退役命名空间检查。
    - 决定：**待用户批准清理点 J**。

### WP3：拆分 DesktopAgentRuntime

先为当前行为增加特征测试，再沿现有职责边界搬迁。优先顺序：

1. 从 `RunTestAsync` 提取测试运行生命周期协调。
2. 从 `RunSupervisedTestWorkerAsync` 提取 Worker 启动、监管、终止和结果接收。
3. 从 `ExecuteCopyBatchStepAsync` 及其相邻恢复方法提取 CopyBatch 执行与 receipt 恢复。
4. 从 `StartTestAsync` 提取准备、授权、活动运行占用和启动状态转换。
5. 将测试步骤排序、支持动作验证、退出码判定等纯规则移入有明确所有权且可测试的组件。

目标结构以职责为准，可复用已经存在的 `AgentTestCoordinator` 等组件，不得为了符合
名称示意而创建重复层。最终 `DesktopAgentRuntime` 只保留进程级组合、请求路由、跨用例
生命周期和薄委派，不再直接包含完整工具执行循环或 CopyBatch 恢复算法。

禁止：

- 创建 Service Locator、万能依赖容器或仅隐藏构造参数数量的聚合对象；
- 建立覆盖所有 Agent 操作的通用 Manager/Command Bus；
- 在搬迁同时改变 IPC、数据库、测试计划语义或用户可见行为；
- 全局替换 `CancellationToken.None` 或统一吞并 `catch (Exception)`。

### WP4：拆分 TestPage

按现有产品用例将可测试逻辑移出 `TestPage.xaml.cs`：

1. 测试定义与 Mixed Directory 定义构建。
2. Prepare、Start、Cancel、OutcomeUnknown 和权威对账的页面会话协调。
3. 目标选择、工具身份和电源/调度输入投影。
4. 运行历史、Dite 历史、统一比较和导出协调。
5. System Support review 请求构建和结果投影。
6. 用户预设加载、保存、删除及选择状态。

WinUI code-behind 保留控件事件、页面生命周期、Dispatcher/picker/dialog 等原生适配。
`async void` 可以保留为 WinUI 事件入口，但入口必须薄、异常必须被观察，并立即委派给
可等待的方法。不得为了测试复制一套与页面状态平行的 presenter 模型。

拆分必须保留 V0.39 已实现的稳定 RunId、Start/Cancel unknown outcome、防重复提交、
页面离开 cancellation 和 Agent 对账语义。

### WP5：拆分 SettingsPage

至少分离以下职责：

1. 数据位置切换、Agent 停止、迁移排他锁、失败恢复和替代进程启动。
2. 外部工具检测、路径配置、portable 安装和 MSI 安装协调。
3. 主题、强调色、语言、执行模式和隐私偏好保存。
4. 启动项、About、更新和反馈入口。

本工作包必须重新评估 TD-803，但仅实现拆分所需且能够被证据覆盖的状态语义。
如果发现安装 timeout、UAC 后继续运行或部分安装需要新的权威查询协议，立即停止该部分，
保持当前行为并请求独立设计决定；不得在本计划中顺带升级 IPC。

### WP6：架构测试和防回退规则

1. 保留并强化项目依赖方向、四进程边界、SQLite 所有权、typed IPC、禁止自由命令、
   deny-by-default 和真实 mutation 禁令。
2. 为 C# 与 XAML 增加退役命名空间检查，避免只扫描 `*.cs`。
3. 将页面文案、普通控件布局、具体私有方法名和易变文件文本从架构测试移到对应的
   行为测试或原生人工验证。
4. 优先使用程序集引用、类型关系、反射和结构化 XAML 解析；只有无法结构化表达的
   永久禁令才允许稳定文本扫描。
5. 增加防回退检查：三个热点文件不得重新吸收已经提取的职责；新内部接口必须有真实
   消费者、边界用途或明确测试替身。

防回退规则是审查护栏，不设置机械的行数、方法数或构造参数硬阈值。指标可触发审查，
但不能通过拆文件、依赖聚合或空壳接口规避。

## 4. 明确不做

- 不开始 V0.4 视觉、美术、布局或新交互实现。
- 不新增页面、产品功能、测试引擎、监控能力或管理操作。
- 不合并 App、Agent、TestWorker 或 ElevatedBroker。
- 不为了减少项目数量合并 Domain、Inventory、IPC 或 Monitoring 项目。
- 不全面重写 `NamedPipeAgentConnection` 或 `WinPoolSqliteStore`；只有治理导致的最小接线
  调整可以进入本计划。
- 不建立 MVVM 框架迁移、公共 SDK、插件系统、通用 Repository、通用 Command Bus、
  通用 UI Guard 或全局诊断平台。
- 不批量修复全部 `async void`、`CancellationToken.None`、`catch (Exception)` 或命名风格。
- 不处理与三个热点类和已确认无用代码无关的一般技术债务。
- 不优化发布包体积，不改变 self-contained 部署策略。
- 不直接删除文件，不清理测试证据，不重组 Dite、Research、Tests 或其他项目。

## 5. 自动验证

每个工作包至少执行直接受影响测试；每个可合并检查点必须从仓库根目录执行完整门：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

要求：

- 全部确定性测试 `passed`，0 failed、0 skipped；测试数量按实际输出记录，不预写目标数。
- Release build 为 0 error、0 warning，或记录用户批准的明确例外。
- 依赖漏洞审计未发现已知漏洞。
- 没有新的项目依赖环、App 直写 SQLite、自由命令入口或安全边界退化。
- 删除/移动候选后重新执行全仓引用、XAML、序列化和协议检查。
- 工作树中与本计划无关的用户文件保持未修改、未暂存。

## 6. 原生和目标化人工验证

架构治理仍可能破坏装配、导航和异步生命周期，至少验证：

1. App 启动，Agent 启动并保持托盘生命周期，六个标题栏标签可导航。
2. Test 页 Prepare、Start、状态刷新、Cancel、终态和历史读取保持现有行为。
3. Agent 暂时不可用或响应未知时，不重复 Start/Cancel，恢复后能权威对账。
4. Dite 导入历史、运行导出和 System Support review 路径可打开；不得执行真实存储 mutation。
5. Settings 的主题、语言、执行模式、工具检测和数据位置界面保持可用。
6. 标准用户、UAC、真实外部安装器、真实数据位置迁移、设备和长时间案例如未实际执行，
   必须保持 `unverified`。

所有结果只使用 `passed`、`failed`、`unverified`、`not_required` 或
`deferred_by_user`。自动测试不得替代原生、UAC、设备或长期运行证据。

## 7. 停止条件

出现以下任一情况时停止对应工作包并报告用户，不得继续扩大范围：

- 必须改变 IPC protocol、消息 schema 或 SQLite schema 才能完成拆分；
- 必须改变四进程所有权、安全授权、数据脱敏或真实 mutation 边界；
- 候选类型存在反射、序列化、历史数据或外部消费者，无法证明安全移除；
- 拆分要求同时重写第二个无关产品流程或建立全局框架；
- 特征测试揭示当前行为本身存在数据损坏、重复副作用或授权漏洞；
- 自动门出现无法归因于当前小批次的回归；
- 发现必须直接删除未在本计划精确列出的完整文件或测试证据；
- 工作区出现与本计划重叠的用户修改，无法安全保留。

## 8. Git、文档与完成门

实施采用小批次、可独立回退的提交顺序：WP1 → WP2 → WP3 → WP4 → WP5 → WP6。
一个工作包可以拆成多个职责明确的本地提交，但不得把代码搬迁、行为修改、文档勘误
和发布动作混成一个提交。

只有以下条件全部满足，才能请求用户确认治理完成：

- 六个工作包的范围完成，或未完成项具有用户明确接受的风险和延期决定；
- 所有候选代码都有“移除、保留、移动到测试、被现有实现替代”之一的证据结论；
- 三个热点类已达到第 1 节的职责目标，没有通过新 God Object 转移复杂度；
- 完整自动门通过，实际测试数量记录一致；
- 目标化原生结果如实记录，未执行案例没有被标记为 passed；
- README、CHANGELOG、Product、Development、Quality 和实现状态不存在当前事实冲突；
- 产品版本仍为 V0.39，IPC 仍为 3，SQLite schema 仍为 12；
- CHANGELOG 只记录实际结果，Plan 中保存每个工作包的实施与证据记录；
- 没有未经授权的 push、tag、GitHub Release、二进制上传或部署；
- 用户确认完成后，本计划冻结到 `docs/Archive/V0.39-architecture-hardening/`，更新归档
  索引并移除活动 Plan；随后另建 V0.40 Plan。

## 9. 实施记录

已完成：

- 本地 `main` 已快进到最终 V0.39 提交 `e9f799f`；
- 已从该基线创建 `refactor/v039-architecture-hardening`；
- 初始 Plan 已由 `90bb801` 提交并推送到
  `origin/refactor/v039-architecture-hardening`；`origin/main` 已快进到 `e9f799f`。
- 2026-08-14 的 WP1 自动基线：restore `passed`；Release tests 为 526 passed、0 failed、
  0 skipped；Release build 为 0 warning、0 error；依赖漏洞审计未发现已知漏洞。
- GitHub Release `V0.39` 是正式发布、指向 `e9f799f`，并包含
  `WinPool-V0.39-final-win-x64.zip`（242,873,406 bytes，SHA-256
  `782caa4af10cd781edc524a4d035d88309110967b242ffaea6cf47a429a1bfba`）。现有授权记录
  明确记载 commit、push、tag 和 Release，但未单独定位到二进制资产上传授权；该历史
  证据缺口已记录，未对远端 Release 或资产做任何改变。

已完成的 WP3：

- `DesktopAgentRuntime` 已从约 3,090 行收敛为进程级组合与薄请求路由；测试启动授权、
  完整运行生命周期、TestWorker 监管、CopyBatch 恢复和执行分别归属
  `TestRunStartCoordinator`、`AgentTestRunWorkflow`、`TestWorkerSupervisor`、
  `CopyBatchRecoveryCoordinator` 和 `CopyBatchExecutionCoordinator`。
- `TestExecutionRules` 承担步骤排序、受控系统辅助动作和工具退出码的纯规则；
  `AgentProcessProjection` 承担受监管进程的安全投影。没有改变 IPC protocol 3、schema 12、
  四进程模型或真实 mutation 边界。

已完成的 WP4：

- `TestDefinitionFactory` 已移入 `WinPool.Testing`，拥有 I/O、Copy 与 Mixed Directory
  三种闭合测试定义图及 CopyBatch 边界；`TestPage` 只收集原生控件输入并调用工厂。
- 为避免以 presenter 或平行页面状态模型制造新的技术债，Prepare/Start/Cancel 的确认对话框、
  picker、Dispatcher、事件流订阅、未知结果对账、历史选择和 System Support 结果呈现保持在
  WinUI 页面适配层。它们没有新增领域规则、SQLite 写入或自由命令入口。
- `WinPool.Testing.Tests` 已增加三项工厂回归测试，锁定重复 I/O 步骤、CopyBatch 上限和
  Mixed Directory manifest 上限。

已完成的 WP5：

- 数据位置切换的非 UI 原语已移至 `DataLocationSwitchRuntime`：已停止 Agent 的写入协调、
  命名管道退出等待、每用户迁移互斥、经验证的 `StorageLocationManager` 组合和替代应用启动。
  `SettingsPage` 保留两次原生确认、Agent 重连和用户提示。
- 外部工具检测、路径配置及 portable/MSI 安装仍由现有 typed Agent contract、受控安装器和
  原生确认对话框组成；本次未引入新的安装状态机或查询协议。TD-803 未出现需要 IPC/schema
  升级的 timeout、UAC continuation 或部分安装证据，因此保持原有受控语义。

已完成的 WP6：

- 架构测试现在验证 Runtime 对专责 workflow 的委派、测试定义图的跨层归属和数据位置切换
  的互斥/受控迁移原语；不再要求 Runtime 保留已抽取的私有实现文本。
- 退役 `WinPool.Core` 扫描同时覆盖生产 C# 与 XAML；新增的防回退测试确保 `TestPage`
  不会重新承载定义图和 CopyBatch 边界。
- 2026-08-14 最终自动门：restore `passed`；Release 单进程测试 530 passed、0 failed、
  0 skipped；Release 单进程 build 0 warning、0 error；依赖漏洞审计未发现已知漏洞。
- 原生最小验证：`passed` App 启动、欢迎对话框关闭、六个标题栏标签发现、Test→Settings
  导航；`unverified` Test Prepare/Start/Cancel/历史、Dite 导入/导出、System Support、
  主题/语言/执行模式/工具检测及数据位置迁移。Settings 自动扫描曾显示失败并保留上次结果，
  未把该环境诊断记为通过，也没有执行真实操作。

已完成的 WP1 文档勘误：

- `README` 已反映 V0.39 最终修正、`main`/tag/Release 现状、526 自动测试基线和当前
  架构治理阶段；中文阅读副本已同步。
- `CHANGELOG` 已将 526 记录为 V0.39 的控制测试计数，并保留 562 表述的前向勘误；
  中文阅读副本已同步。

已完成的 WP2 清理：

- A、B、C 的完整废弃文件已移动到项目根级
  `Rubbish/20260814_winpool_architecture_hardening/Program/WinPool/`，保留原相对路径。
- D、E、F、G、H 的已批准无消费者合同已精确移除，同时保留仍有消费者的共享类型。
- I 的单调时钟实现已移到 `WinPool.Monitoring.Tests` 并改为内部测试 helper；测试覆盖保留。
- J 的退役 `WinPool.Core` XAML 命名空间已移除。
- WP2 后完整 Release 自动测试仍为 526 passed、0 failed、0 skipped。

进行中的 WP3：

- 已抽取 `TestExecutionRules` 和 `TestWorkerSupervisor`；后者拥有 TestWorker 注册、心跳、
  调度恢复和终态持久化。Agent 测试已通过。
- 已抽取 `CopyBatchRecoveryCoordinator`，拥有 manifest 创建、恢复校验和终态验证；Agent
  测试已通过。剩余 CopyBatch 执行与运行工作流继续在 WP3 中拆分。
