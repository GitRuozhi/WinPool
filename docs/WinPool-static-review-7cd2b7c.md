# WinPool 静态检查报告

检查日期：2026-09-15
仓库：GitRuozhi/WinPool
分支：main
固定提交：7cd2b7c7ec8b825238cdff04a8ae750a4b9bd228
项目版本：V0.53；global.json 指定 SDK 10.0.400 / latestPatch。

## 结论与边界

本轮发现 5 项应修复的静态问题：1 项 P1、4 项 P2。P1 表示应优先处理的可用性问题；P2 表示具有明确触发条件的功能、数据正确性或健壮性问题。分级是本次审查判断，不是 CVSS 安全评分。

本轮是固定远端提交上的重点源码审查，不是全仓自动扫描。重点追踪了 Agent 控制通信、进程心跳持久化、监控采样到 SQLite/CSV、模拟文档加载及提交对账，并抽查工程配置和相关测试。没有修改仓库、创建分支、提交、推送或执行真实存储操作；不覆盖用户本地未提交或未推送的代码。

当前执行环境是 Linux，`dotnet --info` 返回 `dotnet: command not found`。源码通过 GitHub 连接器读取；直接克隆因环境 DNS 不可用而失败。因此没有执行 Roslyn/.NET 编译器分析、WinPool 自动测试、NuGet 漏洞审计或 Windows/WinUI 运行验证。仓库 Quality.md 记载的 551/551 测试和 0 警告/0 错误是仓库原有记录，不是本轮结果。

## F01 — P1：一次持久化异常可使 Agent 控制监听永久失效

**定位**

- `src/WinPool.Agent/AgentControlServer.cs:274–295`：监听循环的异常筛选。
- `src/WinPool.Agent/AgentControlServer.cs:461–475`：每次请求在分派前保存心跳。
- `src/WinPool.Agent/Program.cs`，`Main`：启动 `serverTask`，仅在应用退出的 finally 中等待并抑制任务异常。

**证据与触发条件**

请求分派前会调用 `PersistProcessAsync`；Program 将其绑定到 `WorkerProcessRepository.SaveAsync`。外层只处理 EOF、InvalidData、Json 和 IO 等异常，没有处理 `Microsoft.Data.Sqlite.SqliteException`。因此当心跳持久化因磁盘满、SQLite 锁等待耗尽等原因抛出该异常时，异常会穿透 ServeConnectionAsync 和 RunAsync，令唯一的监听任务 fault。

Program 没有在应用运行期间观察该任务的失败并更新状态或恢复服务，只在退出时 SuppressThrowing。其结果不是单个请求返回失败，而是 Agent 进程/托盘可能仍在、控制管道却不再接受新连接。这里不声称 Windows 进程必然崩溃。

**修复建议**

将可恢复的请求/持久化异常隔离到请求或连接边界，记录结构化错误并使后续连接仍可处理；不可恢复错误应显式更新生命周期和通知用户。对 `serverTask` 加运行时故障观察，避免出现“进程存活但控制服务已死”的状态。不要用无记录的 catch-all 掩盖数据库写失败。

**建议回归**

向 `persistProcess` 注入一次 SqliteException，确认错误可见，后续合法客户端仍能连接并获取状态；另测不可恢复故障是否明确进入 Failed 状态。

状态：高置信度源码路径；Windows 故障注入未运行。

## F02 — P2：虚拟磁盘监控指标在持久化时丢失，缺失指标被写成零

**定位**

- `src/WinPool.Infrastructure.Windows/PdhDiskMonitorSource.cs`，`RequestedVirtualDiskValues`。
- `src/WinPool.Agent/DesktopAgentRuntime.cs`，`CreateDefaultMonitorRequest`。
- `src/WinPool.Infrastructure.Sqlite/MonitorSampleBatchWriter.cs:227–239`。
- `src/WinPool.Infrastructure.Sqlite/SqliteMonitorSessionPersistence.cs`，`TryWrite`。
- `src/WinPool.Infrastructure.Sqlite/MonitorCsvExporter.cs`，`ExportAsync`。

**证据与影响**

默认请求包含六类 VirtualDisk 指标，采样器会生成 Active、Missing、Stale、NeedRegeneration、Regenerating 和 PendingDeletion bytes。持久化路径仍只写 activity/read/write/queue 四列。`Metric` 对找不到的指标返回 `0d`。

所以，一个只含六类虚拟磁盘指标的有效采样进入写入器后，六类实际指标均不落库，四个不适用的常规指标反而成为零。CSV 只导出这四列，无法还原虚拟磁盘指标，也无法区分“不适用/未采集”和“真实为零”。这并不意味着实时 UI 的六类指标必然显示错误；问题发生在持久化及导出路径。

**修复建议**

使持久化契约覆盖实际接受的全部指标，并保存适用性/有效状态；缺失值使用 NULL 或明确状态，而不是零。如果某些指标暂不支持历史保存，应明确拒绝/标记，不能静默丢弃并生成全零记录。涉及 schema 的变动需按项目格式策略执行，不擅自迁移或删除旧数据。

**建议回归**

用六类不同且非零的虚拟磁盘指标完成采样→持久化→读取/导出往返；再用缺少 read/write 的采样验证缺失值没有变成零。

状态：源码路径确认；独立 Python 映射模型复现了“丢失六项、生成四个零”；不是 C# 持久化集成测试。

## F03 — P2：模拟文档列表没有分页，总响应可超过 IPC 单帧上限

**定位**

- `src/WinPool.Infrastructure.Sqlite/SimulationDocumentRepository.cs`，`ListAsync`。
- `src/WinPool.Agent/DesktopAgentRuntime.cs`，`ListSimulationDocumentsAsync`。
- `src/WinPool.Infrastructure.Windows/StorageSystemRepository.cs`，`AgentBackedStorageSystemRepository.LoadSimulationsAsync`。
- `src/WinPool.Ipc/IpcProtocol.cs:7–11`；`IpcFrameCodec.WriteAsync`。

**证据与影响**

`ListAsync` 一次读取所有文档的完整 JSON，Agent 将其合并成单一响应。IPC 限制整个序列化帧最多 4 MiB；超过时 WriteAsync 抛 InvalidDataException，控制连接结束，客户端拿不到列表。

单份文档已在 StorageDocumentTransportBudget 中计算转义后的大小，并限制为 3 MiB；这项保护是存在的。本问题是多份合计无预算/分页，不是遗漏了单份 JSON 转义开销。随文档数量或大小增加，完全可能出现“单份都在预算内，列表却始终无法读取”的状态。

**修复建议**

列表只返回元数据，按 documentId 获取正文；或实现按序列化字节预算分页的完整列表协议。保留帧上限并返回明确可理解的超限错误，不应仅无限上调常量。

**建议回归**

构造两个每个约 2.25 MB、单份预算内的文档，确认列表能分页/按需获取；覆盖大批小文档和边界字节数。

独立体积模型结果：两个仓储信封样本的正文各为 2,250,119 bytes，Python JSON 表示的 typed payload 各为 2,250,372 bytes；列表尚未加入 IPC 信封就达到 4,500,761 bytes，大于 4,194,304 bytes。该模型不是完整 UI 文档构造器，也不是 System.Text.Json 的精确字节测试；其作用是展示聚合体积反例。

## F04 — P2：CommitId 重试没有校验请求绑定，查询返回的是可变的当前文档

**定位**

- `src/WinPool.Infrastructure.Sqlite/SimulationDocumentRepository.cs:110–158`。
- `src/WinPool.Infrastructure.Windows/StorageSystemRepository.cs`，`SaveEditAsync` 的 OutcomeUnknown 分支与 `UpdateHash`。
- 抽查的 `tests/WinPool.Persistence.Tests/SimulationDocumentRepositoryTests.cs` 覆盖了乐观更新、原子提交、失败回滚；该文件没有验证相同 CommitId 配不同内容的行为。未声称全仓其他测试均无此覆盖。

**证据与触发条件**

`CommitEditAsync` 在验证当前 document/hash/plan 前，仅凭 CommitId 找到已有记录就返回，没有比对本次 documentId、预期旧哈希、提交后哈希和计划身份。对于同一文档复用 CommitId 但改变提交内容的请求，可能返回先前结果作为成功，而新的内容根本没有提交。

此外，`FindByCommitIdAsync` 将提交表 JOIN 到 `simulation_documents`，读的是文档“当前” JSON/hash/revision，而非那次提交的不可变结果。提交 A 产生 revision 2，后续编辑到 revision 3 后，查询 A 返回 revision 3。客户端 OutcomeUnknown 分支直接采用返回哈希，并没有核验其与本次预期提交的绑定。

**修复建议**

把 CommitId 与不可变请求指纹绑定，至少包含 documentId、beforeHash、afterHash、目标 revision 和 plan/operation 身份；同键不同请求明确冲突。同键同请求返回原始提交回执。对账应返回提交表的确定结果，并把“文档已更新到更晚版本”作为独立事实；客户端验证目标身份及预期结果，不把当前文档哈希当成旧提交结果。

**建议回归**

相同 CommitId/相同请求重试不重复执行；相同 CommitId/不同内容必须冲突；文档继续编辑后查询旧 CommitId，仍返回旧提交的准确回执。

状态：源码路径确认；用原查询在内存 SQLite 中复现“提交记录 revision 2、查询结果 revision 3、哈希不等”。不是 WinPool C# 端到端对账测试。

## F05 — P2：控制管道在身份校验前没有握手读取超时

**定位**

- `src/WinPool.Agent/AgentControlServer.cs:274–305`。
- `src/WinPool.Ipc/CurrentUserPipeFactory.cs`，`CreateServer`。
- `src/WinPool.Ipc/IpcFrameCodec.cs`，`ReadAsync` / `ReadExactlyAsync`。

**证据与影响**

控制服务单实例、串行处理连接。客户端连接后，服务先等待整个握手帧，再进行 nonce、时间和进程身份校验；读取只接收 Agent 生命周期 token，没有握手专用 deadline。IpcProtocol.MaximumHandshakeAge 限制的是已收到握手的时间戳，不是等待收到它的时长。

符合当前用户 ACL 的本地进程只要连上后不发数据，或只发半帧并保持连接，就可以占住唯一控制连接。其他正常请求无法进入，直到该客户端断开或 Agent 被终止。范围是同用户本地阻塞风险，不是远程未授权访问，也不是跨用户提权。

**修复建议**

为建立握手和接收完整帧设置独立、受限的读取期限；超时丢弃该连接并继续监听。不要把长耗时合法采集和握手读取共用一个粗暴的短超时。

**建议回归**

客户端零字节/半帧停发的情形均应在限期后释放连接；随后合法客户端能够正常握手。Windows 命名管道实测本轮未运行。

## 本轮执行与未验证项

| 项目 | 本轮结果 |
|---|---|
| 固定提交源码检查 | 已执行；发现上述 5 项问题 |
| SQL、映射和聚合体积独立模型 | 已执行；3 类反例复现 |
| .NET SDK 检查 | 不存在 dotnet；环境为 Linux |
| 编译 / Roslyn 分析器 | unverified |
| WinPool 自动测试 | unverified |
| NuGet 漏洞审计 | unverified |
| Windows / WinUI / 命名管道运行验证 | unverified |
| 真实存储写操作 | not_required，未执行 |

配套 `model_checks.py` 只使用标准库、合成数据和内存 SQLite，运行命令为：

```text
python model_checks.py
```

它复核三个局部性质，不能替代 WinPool 原始测试，也不能据此宣布整个项目通过。原始输出见 `model_results.json`。

## 修复顺序

先处理 F01 的服务故障隔离和 F02 的监控数据失真，再处理 F03 的协议分页与 F04 的提交回执；F05 随控制服务修复一起补入异常连接测试。完成修复后，在隔离的 Windows 工作副本和 global.json 指定 SDK 下执行项目 Quality.md 的构建、测试及依赖审计，不覆盖用户正在运行的输出目录。
