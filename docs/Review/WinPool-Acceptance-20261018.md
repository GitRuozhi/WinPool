# WinPool V0.61 验收：对《全量检视报告》的闭环判定

日期：2026-10-18
被验收对象：`main` / `f1e103f`（V0.61 / 0.6.1.0），工作树仅 `Ref/` 与审查文档未跟踪
对照文件：`docs/Review/WinPool-Full-Review-20261017.md`（2026-10-17 全量检视）
验收方式：**独立复跑**，不采信既有汇总。全部命令从仓库根执行。

---

## 0. 总判定

| 维度 | 判定 |
| --- | --- |
| 冗余与死代码 | **已闭环**。上一轮点名的 7 项死代码全部消失，`src` 减 5 275 行，文档减 46%。 |
| 运行时失控缺陷 | **已闭环**。对账重放循环、稳态轮询、清单双传、静默跳过均已修复，并经源码核对。 |
| 性能（用户核心诉求） | **未闭环**。根因（每次采集新起 PowerShell 进程）按要求被**显式推迟**；单次采集仍 2 889 ms，单次真实写入仍 12.54 s。 |
| 验证门 | **未闭环**。我独立复跑得到 **2 504 项中 1 项失败**，红灯是上一轮已点名的同类缺陷。 |
| 产品可用性 | 通过。候选包实际启动、Agent 拉起、无 UAC 提示、手动刷新成功、正常关闭。 |

**一句话：把"该删的"删干净了，把"该快的"留着没做，验证门仍然红着。**

---

## 1. 我实际做了什么

| 项 | 内容 |
| --- | --- |
| 构建 | 从仓库根 `dotnet restore` + `dotnet build -c Release --no-restore -m:1` |
| 测试 | 11 个测试项目**逐个** `dotnet test --no-build`，每个出独立 TRX；对失败项单独隔离复跑一次 |
| 源码核对 | 逐条对照上一轮 P1–P6、0.x、2.x 项；统计接口数、死代码符号、文本断言数 |
| 产品冒烟 | 从候选包解压目录以普通权限启动，走完欢迎窗、导航、手动 Storage 刷新、关闭 |
| 独立计时 | 从 `operation-timing.jsonl` 按本轮 `processId` 读取真实采集耗时 |

证据目录：`artifacts/test-results/acceptance-20261018/`（`build.log`、`trx/`、`arch-rerun/`、`acceptance-test-summary.json`）。

---

## 2. 构建与测试的独立结果

### 2.1 构建：绿

```
dotnet restore WinPool.slnx                                   exit 0
dotnet build WinPool.slnx -c Release --no-restore -m:1        exit 0
                                                              0 警告 / 0 错误
                                                              MSBuild 42.30 s
```

### 2.2 测试：红

| 项目 | total | passed | failed | NotExecuted |
| --- | ---: | ---: | ---: | ---: |
| WinPool.Infrastructure.Tests | 984 | 983 | 0 | 1 |
| WinPool.App.Tests | 461 | 461 | 0 | 0 |
| WinPool.Application.Tests | 441 | 441 | 0 | 0 |
| WinPool.Persistence.Tests | 193 | 190 | 0 | 3 |
| WinPool.Agent.Tests | 156 | 156 | 0 | 0 |
| WinPool.Execution.Tests | 113 | 113 | 0 | 0 |
| WinPool.Monitoring.Tests | 53 | 53 | 0 | 0 |
| WinPool.Architecture.Tests | 44 | **43** | **1** | 0 |
| WinPool.Agent.Client.Tests | 24 | 24 | 0 | 0 |
| WinPool.Domain.Tests | 23 | 23 | 0 | 0 |
| WinPool.Ipc.Tests | 12 | 12 | 0 | 0 |
| **合计** | **2 504** | **2 499** | **1** | **4** |

墙钟 288.2 s。TRX 原始结果计数：`Passed 2499 / Failed 1 / NotExecuted 4`。

### 2.3 那一项红灯

```
WinPool.Architecture.Tests.ArchitectureBoundaryTests
  .SettingsKeepDeveloperModeOutOfTheAppearanceCardAndStopUpdatingAfterElevationHandoff

ArchitectureBoundaryTests.cs:730
  Assert.Contains("if (_closingForElevationHandoff)", mainWindow, StringComparison.Ordinal);
  → Assert.Contains() Failure: Sub-string not found
```

- `_closingForElevationHandoff` 在 `src` 中**已不存在**；`MainWindow.xaml.cs` 的该分支在 `24b5e96`（V0.61 简化）中被重构掉。
- **确定性复现**：单独复跑 `WinPool.Architecture.Tests` → `1 failed / 43 passed`，exit 1。
- 影响：`docs/Quality.md` 记为"累计 2500 passed／4 explicit skip"，与实测 **2 499 passed／1 failed／4 skip** 恰好差这一项。说明**收口后的重构没有重跑全量门**，汇总停留在某次更早的运行。

### 2.4 这不是新缺陷，而是上一轮点名的同一类缺陷

上一轮 §A5 / 0.7 的结论是：把**对源码文本做字符串断言的测试**当架构门是错误做法，会因控件或文本重构而红。上一轮的两个具体红灯被修掉了，但这一类没有清除：

- `WinPool.Architecture.Tests` 中 `Assert.Contains` / `DoesNotContain` 共 **417 处**，`File.ReadAllText` 共 **103 处**。
- 因此"删掉两个断言"只解决了当时那一例；**根本做法仍应是删除这一类**，或把它改成能编译期失效的强类型检查。

---

## 3. 产品冒烟（候选包，普通权限）

候选：`artifacts/packages/V0.61/20261018T041447677Z-.../WinPool-V0.61-win-x64`

| 步骤 | 结果 |
| --- | --- |
| 从解压目录启动 `WinPool.App.exe` | PID 1404，**< 4 s 出现欢迎窗** |
| Agent 自动拉起 | PID 29200 |
| UAC / 安全警告 | **无提示**（上一轮遇到的"无法验证发布者"已消失） |
| 导航可用 | 约 5 s 后全部页面可选 |
| 手动 Storage 刷新 | 成功 |
| 关闭主窗口 | App 退出，Agent 留托盘（设计如此） |

**结论：产品本身可用。红灯是测试腐化，不是产品故障。**

---

## 4. 独立计时：性能根因未动

本轮候选、普通权限、手动 Storage 刷新，按本轮 `processId` 读取：

```
inventory.queue              0 ms
inventory.provider.collect  2 889 ms   ← 一次 PowerShell 子进程
inventory.collect           2 904 ms
merge / project / persist   2 / 0 / 31 ms
```

- **手动刷新已收成 1 次采集**（上一轮是 3 次，更早是 5 次）——这部分确实改进。
- **但单次采集仍是 2 889 ms**。上一轮我的 p50 是 3 098 ms。**单位成本没有变化**，因为每次采集仍然新起 `powershell.exe`。

真实单写（`docs/Quality.md:106`，操作 `40757c3c…`）：

| 阶段 | 耗时 |
| --- | ---: |
| prepare | 3.536 s |
| preflight | 3.324 s |
| execute | 5.682 s |
| **合计** | **≈ 12.54 s** |
| 其中底层 `inventory.provider.collect` | 3 次：3.414 / 3.277 / 3.409 s |

对照上一轮实测的同一类操作：15.4 s、5 次采集。**改善来自 5→3 次采集，不来自每次更快。** 一次改名仍要 12.5 秒，而 `pwsh` 里是 1～2 秒。

---

## 5. 对上一轮审查逐项验收

### 5.1 已闭环

| 审查项 | 状态 | 证据 |
| --- | --- | --- |
| **P2** 重复采集 | 已收口 | `ExecuteStepAsync` 改用 `preflight.CallContext.Before/.Safety`，不再自行采集（`WindowsRealStorageBackend.cs:258-292`）；`real.accept.preflight` 已不存在。单写 5→3 次，刷新 3→1 次，达成计划 §4 的三次预算 |
| **P3** 对账无限重放 | 已修 | `ListRecoverableAsync` 改用 `LightStatusAsync` + `ListUnfinishedRealOperationIdsAsync`，不再自动 `ScheduleReconciliation`；改为显式 `ReconcileAgentRealOperationRequest`；App 侧 `while(IsBlocked)` + 1→5 s 轮询已删除 |
| **P5** 稳态 ≥6 次/秒快照轮询 | 已修 | `MonitoringService` 改为事件驱动 `WatchAsync`，仅在 reseed / 需补窗 / 溢出 / 断连时 `RefreshRemoteSnapshotAsync`；源码注明 "no steady-state control polling" |
| **P6** 清单文档双传 + 3 次解码 | 已修 | `ManageInventoryCaptureResponse` 只携带 `(SnapshotId, DocumentId, Sha256, generation)`，正文只走 `AgentInventoryUpdatedEvent`（`AgentInventoryCoordinator.cs:217-223`） |
| **2.1** `WinPool.Inventory` 项目 | 已删 | `src/WinPool.Inventory`、`tests/WinPool.Inventory.Tests` 均不存在；项目数 23→21 |
| **2.2–2.4** 死代码 | 已删（7/7） | `NativeWindowsInventoryProvider`、`EmbeddedPowerShellInventoryProvider`（类壳）、`LocalMachineRecordService`、`CaptureAgentInventoryRequest`、`LoadAgentManageInventoryRequest`、`AgentMonitorSampleEvent`、`InventoryComparer` 在 `src` 中**各 0 命中** |
| **0.6** 静默跳过 | 已修 | `RealAdmissionReadOnlyTests` 改为 `[EnvironmentEnabledFact("WINPOOL_READ_ONLY_ADMISSION")]`，记为显式 skip |
| **0.8–0.10** fixture / flaky | 已修 | 本轮 Infrastructure 984 项 **0 失败**，Persistence 193 项 **0 失败**；上一轮的 30 项与 2 项必现 flaky 均消失 |
| **0.2** App 恢复轮询 | 已修 | 见 P3 |
| **5.1–5.2** 文档瘦身 | 显著 | 现行文档 285 765 → **153 718 字符（−46.2%）**；`Quality.md` 重构为"最小充分验证 / 当前证据与缺口 / 后续命令 / 口径"，历史移入 `Archive` |
| 测试污染用户目录 | 已修 | 测试改为 `TestDiagnostics` 重定向到 `Trace.WriteLine`，本轮 `operation-timing.jsonl` 只新增我这次真实刷新的记录 |

### 5.2 未闭环

| 审查项 | 状态 | 依据 |
| --- | --- | --- |
| **P1** 每次读取新起进程（我判定占 75%） | **未做** | `src` 仍无 `Runspace` / `PowerShell.Create`；`InventoryPipeline.cs:37`、`WindowsRealStorageCommandAdapter.cs:382` 仍 `new Process`。单次采集 2 889 ms 未变 |
| **P4** `PostCallWindow` 60 s / `PostCallPoll` 2 s | **未做** | `WindowsRealStorageBackend.cs:93-94` 原值未变 |
| **0.5** 写后验证在循环里整份重采 | **未做** | `:368-424` 结构未变；只是加了 `CaptureWithinWindowAsync` 的时限包装 |
| **0.7** 源码文本断言 | **部分** | 已指向不可变归档，但 417 处仍在，且本轮新增 1 项红灯 |
| **5.5** 性能回归测试 | **未做** | 全仓无任何耗时/采集次数预算断言；`CaptureCount` 只被断言 `> 0` |
| **4.1** 接口收敛到约 15 | **部分** | 80→70；单一实现 52→48；全仓零实现 17→14 |
| 验证门全绿 | **未达成** | 见 §2 |

---

## 6. 关于"推迟 P1"这件事的判断

这不是执行者擅自跳过。已归档的 V0.61 计划 §2 第 4 条明确写：

> **本版不预建常驻PowerShell、控制请求多路分发或SQLite全库重构。** …它们不是首稿前置。核心修改后若仍有独立瓶颈，在最终结果记一行具体计时和建议；扩大这些实现前由用户决定。

计划 §4 也把目标定成"三次采集"，而实测正好三次。**所以就计划自身口径，V0.61 是达标的。**

但计划同时要求："若仍有独立瓶颈，在最终结果记一行具体计时和建议"。

我核对的结果是：**计时记了，结论没记。**

- 已记录：`Quality.md:142` "本次 provider 2.769 s"；`Quality.md:106` "3 次采集 … prepare 3.536 / preflight 3.324 / execute 5.682 s"；归档计划 §6 "单写底层采集恰 3 次；候选 Medium 手动 Storage provider 约 2.53 s"。
- 未记录：**没有一行把它认定为"仍存在的独立瓶颈"，也没有给出"常驻宿主"这个建议**。现行文档里唯一提到 runspace 复用的是 `Reference/Pool-VD-Timing-20261016.md:43`，那是 2026-10-16 的旧建议，早于 V0.61，且写在"评估"语境下。

后果：后续接手者读到的是"3 次采集、12.54 s，已达标"，而不是"3 次采集已达标，但单次 2.9 s 仍是主要瓶颈，下一步应做常驻宿主"。这一项交接信息缺失，是客观的收口缺口。

同时要指出：把 P1 推迟，等于把用户最初的核心抱怨（"pwsh 几秒、WinPool 数百秒"）原样带进公开测试版。这个取舍只能由用户决定，我在此只陈述代价。

---

## 7. V0.70 公开测试版条件评估

`Product.md` 的口径是：首稿公开测试版在 V0.70 之前交给不同环境的专门测试人员，不等全部平台矩阵。按此口径逐项判定：

### 7.1 阻塞项（发布前必须解决）

| # | 条件 | 现状 | 需要做什么 |
| --- | --- | --- | --- |
| B1 | 自动门全绿 | **红 1 项** | 修或删 `ArchitectureBoundaryTests.cs:730`；建议同时清掉这一类文本断言的最小集 |
| B2 | 全量门是**一次干净运行**，不是多轮拼接 | 未达成 | 修完 B1 后从仓库根跑一次 11 个项目，把这份 2 504 的单一结果写进 Quality，替换"累计 2 500"口径 |
| B3 | 对性能作出明确决定 | **未决定** | 二选一：<br>① 做 P1（实测 12.7× 上限），单写预计 12.5 s → 约 1.5 s；或<br>② 明确接受 12.5 s/单写、2.9 s/刷新，并在 `TESTING.md` 里写明，避免外部测试者把它当新 bug 报回来 |
| B4 | 至少一项性能预算断言 | **无** | 加 1 项：单写底层采集 ≤ 3 次、手动刷新 ≤ 1 次。不需要真实硬件，合成 fixture 即可 |
| B5 | 候选包从**干净源码**打包 | 包内标 `Source state: dirty` | 提交后从干净 `HEAD` 重新打包并更新 SHA-256 |

### 7.2 可带入 beta（已如实披露，可接受）

| 项 | 现状 |
| --- | --- |
| 跨 Windows 版本 / SSD·SCM / 多成员 / Mirror·Parity / 多层 MAX / 长时 / DPI 矩阵 | `TESTING.md` 已明确交给外部专门测试，未声称已通过 |
| UAC 安全桌面确认 | 未自动化；`Product.md` 如实说明 |
| 无 MSIX / 签名 / Store | 便携 zip，`TESTING.md` 已说明 |
| 无遥测 / 反馈系统 | 靠 `TESTING.md` 的反馈模板 |

### 7.3 判定

> **当前不能发公开测试版**，原因只有一个：门是红的（B1/B2）。产品本身可用（§3 已实测）。
>
> B1+B2 是半天量级的工作。B4 是几小时。**做完这三项即可发 beta**，代价是 beta 会带着 12.5 秒的真实写入延迟。
>
> 若要 beta 不带着用户最初的抱怨发布，则需追加 B3①（常驻宿主）。这是唯一能带来量级变化的改动，也是我上一轮判定占 75% 的那一项。

### 7.4 距离 0.70 还需要什么

0.62–0.69 是可选的（`Product.md` 写明"仅在未来确需迭代时收紧范围，首稿不为用完版本号等待"）。按投入产出排序：

1. **B1 + B2 + B5**（半天）：让门变绿、让汇总可信、让包干净。这是发 beta 的最低责任。
2. **B4**（几小时）：加一项采集次数预算，防止 5→3 的成果再次退化。
3. **B3① 常驻 PowerShell 宿主**（1–2 天）：单次采集 2 889 ms → 约 200 ms 量级；单写 12.5 s → 1.5～2.5 s。这是我上一轮实测 12.7× 的唯一来源。
4. **0.5 / P4**（半天）：写后验证从"60 s 窗口内反复整份重采"改为"先单对象查询、退避、再升级"。最坏情况由 66～115 s 降到 < 10 s。
5. **0.7 收口**（几小时）：把 417 处文本断言压到少量稳定的设计约束，或改为强类型检查。

---

## 8. 结论

1. **冗余维度：闭环。** 上一轮点名的死代码 7/7 删除，`src` 92 010 → 86 735 行，文档 −46%，接口 80→70，静默跳过改为显式 skip。这是扎实的减负。
2. **稳定性维度：闭环。** 我现场复现过的对账失控循环已消除；稳态轮询、清单双传一并修掉。这一项如果留着，beta 必然被"点了没反应"淹没。
3. **性能维度：未闭环，且是显式推迟。** 单次采集仍 2 889 ms，单写仍 12.54 s。改善全部来自"少采几次"，没有"每次更快"。
4. **验证维度：未闭环。** 我独立复跑得 2 499 通过 / 1 失败 / 4 跳过，红灯恰好是上一轮要求清除的同类缺陷，且证明收口后没有重跑全量门。
5. **产品维度：可用。** 候选包启动、Agent 拉起、无 UAC 提示、刷新成功、正常关闭，全部实测通过。

**给公开测试版的建议：先花半天把门修绿并重打干净包（必须），再决定是否接受 12.5 秒（产品取舍）。若要 beta 不重演最初的性能抱怨，就做常驻宿主——这是唯一一个能改变量级的改动。**

---

## 9. 本次验收没有做的事

- 没有修改任何产品源码、测试或文档（除本文件）。
- 没有执行真实磁盘写入；只做了只读刷新。WDC 未被我触碰，末态未变。
- 没有重测 MAX、结构 Apply、模拟提交、数据根迁移、20 Hz 长时监控——这些沿用既有记录，未独立复核。
- 没有验证跨 Windows 版本、SSD/SCM、多成员、冗余布局——本机不具备。
- 没有在提权（High）模式下复测候选；本次为普通权限。
- 没有提交或推送；`git status` 仅新增审查文档与既有 `Ref/`。
