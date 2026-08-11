# WinPool 开发指南

[English](Development.md) | [简体中文（仅供阅读）](Development.zh-CN.md)

> 本文件仅为中文阅读副本；开发规则以无 `.zh-CN` 后缀的
> [Development.md](Development.md) 为准。

## 技术与部署

WinPool 使用 C#、WinUI 3、.NET 10、Windows App SDK，以及已有充分理由时使用的 CommunityToolkit 组件。部署目标为无打包、自包含的 Windows x64 应用。SDK 固定在 `global.json`；唯一项目版本定义在 `Directory.Build.props`。

产品包含四个进程：

- `WinPool.App`：WinUI 外壳、页面、表现层适配器和用户交互。
- `WinPool.Agent`：可见的每用户托盘运行时、SQLite 单写入者、采集、监控、协调和生命周期所有者。
- `WinPool.TestWorker`：隔离、受监督地执行登记测试计划。
- `WinPool.ElevatedBroker`：执行经过审阅的类型化 R3 辅助动作的一次性进程。

## 仓库结构

```text
README.md
README.zh-CN.md
AGENTS.md
Directory.Build.props
global.json
WinPool.slnx
docs/
  Product.md
  Development.md
  Quality.md
  Plan.md                         仅在存在活动阶段时出现
  CHANGELOG.md
  Reference/
  Archive/
build/
  Publish-Staged.ps1
assets/                           纳入 Git 的软件引用资源
OriginArtWork/                    忽略的用户手动艺术源文件
local-assets/                     忽略的开发者本地资源
src/
workers/
tests/
```

当前结构不包含根 `Plan` 或根 `DEVELOP.md`。

## 依赖和所有权模型

依赖方向为表现层和端口 → Application → Domain。

- Domain 保存标识和纯存储规则。
- Execution 保存不可变计划、风险分类、授权、前置条件、策略评估、模拟、重放和明确拒绝。
- Application 拥有稳定的内部用例契约和投影。
- Infrastructure.Windows 拥有只读 Windows 集成和经过审阅的系统辅助端口。
- Infrastructure.Sqlite 拥有持久化实现；正常 App 代码不直接写 SQLite。
- Agent.Client 与 Ipc 拥有封闭的 App 到 Agent 传输。
- Inventory、Monitoring、Testing、Testing.Tools 和 ToolManagement 分别拥有自身模型和类型化适配器。
- App 使用 Application 契约和表现模型。
- Agent 持有数据库写入租约并监督 Worker 与 Broker 子进程。

这些契约均为内部契约。V0.3 不冻结公共 API、插件契约、IPC 线协议或 C#/Python 互操作格式。

## 持久化和进程生命周期

标准数据根为 `%LocalAppData%\WinPool`。便携模式使用可执行文件旁可写的 `Data` 目录。标准根中的 `storage-location.json` 指针选择模式；迁移保留旧副本并验证目标。

正常启动使用 Agent 独占的 SQLite v10 保存采集、工作区状态、模拟文档、监控、测试历史、证据和恢复数据。JSON 仅用于明确支持的无 Agent 开发回退。

WinPool 通过 Windows App SDK 应用生命周期实现单实例。普通重复启动激活已有窗口。批准的提权交接在提权后继进程占用实例键前等待旧进程退出。执行模式永不持久化。

## 执行和外部工具边界

策略和执行器行为都拒绝真实存储结构修改。模拟编辑和只读发现是正常能力。

文件测试需要明确登记目录和本次运行拥有的文件。DiskSpd、fio、Dite、RoboCopy 和 RAMMap 是外部安装。适配器必须校验固定身份、目标、参数和输出语义；不得提供自由命令入口。

内嵌 PowerShell 采集固定且只读，在原生采集器具备等价字段、身份和降级证据前继续保留。

## 构建、测试和 staging

在仓库根执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

可复现自包含 staging 必须使用仓库外的新路径，并拒绝覆盖已有路径：

```powershell
.\build\Publish-Staged.ps1 `
  -OutputPath ..\..\Rubbish\YYYYMMDD_winpool_staging\Program\WinPool `
  -Configuration Release
```

必需布局为：

```text
WinPool.App.exe
Agent/WinPool.Agent.exe
Agent/TestWorker/WinPool.TestWorker.exe
Agent/Broker/WinPool.ElevatedBroker.exe
```

Staging 不得包含重复子进程可执行文件、脚本、艺术源文件、未引用的本地资源、SQLite 文件、测试结果、外部工具或发布元数据。应用明确使用的软件资源可以进入 staging。生成输出只作证据，永不提交。

## 版本推进

产品版本采用 `Va.bc`：

- `a`：大版本；
- `b`：小版本架构/产品线；
- `c`：小版本内的一位迭代编号。

架构和路线图通常只写到 `Va.b`。迭代编号根据实际工作分配且不能超过 9。普通迭代在本地提交；远端推送、tag 和 release 遵循 `AGENTS.md` 与活动 Plan 的授权规则。

`Va.bc` 是唯一项目版本体系。`Directory.Build.props` 根据 `a`、`b`、`c` 机械生成 .NET 和 Windows 必需的数字字段；这些字段只是编译元数据，不具有独立版本含义。数据库 schema 修订号、算法 ID 和 IPC 兼容标识不会重新定义项目版本。

## 文档生命周期

每项事实只有一个所有者：

- Product：长期目标、非目标、边界和路线图。
- Development：架构、模块所有权、环境、构建、版本、Git 和文档流程。
- Quality：稳定质量门、结果词汇、验收类别和例外。
- Plan：唯一活动阶段、当前决定、工作、证据和完成条件。
- CHANGELOG：实际发生的结果。
- Archive：已完成、已替代或已失效的历史状态。
- Reference：非权威的外部或跨项目方法。

当前用户决定高于通用项目管理参考。归档内容是只读历史，不能仅因内容详细就变成当前要求。

无后缀 Markdown 是权威文件；匹配的 `.zh-CN.md` 仅为中文阅读副本，并必须指出对应权威文件。原文已经是中文的文档无需再复制。权威文档变化时，同一工作项内同步阅读副本；阅读副本不控制行为、验收、状态或历史。

用户确认一个阶段完成时，应更新 CHANGELOG，把 Plan 以真实最终状态冻结到 Archive，更新 Archive 索引；若无下一阶段则移除活动 Plan。Tag 和 release 始终需要单独授权。

## 贡献边界

- 保持默认拒绝执行和进程所有权模型。
- 未经单独确认的计划和可废弃环境，不得增加真实存储修改。
- 软件使用资源放入受控 `assets`。
- 受控代码不得依赖被忽略的 `OriginArtWork` 或 `local-assets`。
- 不通过相对路径、复制活动文件、子模块或运行时导入把 WinPool 耦合到其他仓库。
- 路径移动、行为变化、测试和发布动作应可独立审阅。
