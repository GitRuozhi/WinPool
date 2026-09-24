# WinPool 独立审查报告（Zcode）

- **审查对象**：WinPool（`D:\Coding\Research03_WinPool\Program\WinPool`，GitHub: GitRuozhi/WinPool）
- **被审版本**：V0.56（`FileVersion 0.5.6.0`，`Directory.Build.props` 0/5/6），提交 `5872374`
- **审查日期**：2026-09-24
- **审查环境**：Windows 10 22H2 (10.0.19045) x64，.NET SDK 10.0.401，标准 `artifacts\Release` 运行树
- **审查者**：Zcode（独立审查官角色；主代理直接执行 + 4 个只读子代理并行取证）
- **报告定位**：独立第三方意见，不修改产品代码、不改产品决定，只给出证据与建议

---

## 0. 执行摘要

**总体判断：这是一份工程质量与文档诚实度显著高于同类个人项目的代码库，但当前版本存在三处必须收口的“门与边界”问题——自动测试门在 HEAD 上是红的、崩溃可观测性对一类真实故障失明、以及“运行中的便携目录会被构建/清理流程替换”的流程耦合。产品侧的定位、安全承诺与实现基本一致，主要短板在对外可用性与发布合规，而非功能欺骗。**

结论性评级（满分 5）：

| 维度 | 评级 | 一句话依据 |
| --- | --- | --- |
| 架构与分层 | ★★★★☆ | 11 项目依赖方向与文档一致，纯层禁令有测试守卫；文档模块表存在漂移 |
| 代码质量 | ★★★★☆ | 只读边界、IPC 语义、失败恢复设计优秀；3 处 P2 级并发/异常/DI 缺陷 |
| 安全与信任边界 | ★★★★☆ | 提权模式存在 2 条可用链（详见 F-08/F-09）；默认非提升部署收敛良好 |
| 测试体系 | ★★★☆☆ | 714 项真实行为测试（含真实管道/SQLite/7za）；但架构测试 6/47 红、App 层零直接测试、无 CI |
| 构建与交付 | ★★★★☆ | 0 警告 0 错误、单一版本源、SHA-256 并集合并；构建产物目录与运行目录耦合（F-07） |
| 运行期稳定性 | ★★★☆☆ | 受控环境 3×300 s 无异常；WER 记录 7 次同一签名崩溃，自有日志只覆盖其中 1 类 |
| 产品设计与文档 | ★★★★☆ | 边界声明可执行、验证分级诚实；缺获取渠道、双语漂移、素材合规风险 |
| 无障碍 | ★★☆☆☆ | 主导航/类别/消息列表的 UIA 名称泄漏内部类型名（F-13） |

**必须优先处理（P1，共 4 项）**：

1. **F-01** 架构测试门在 HEAD 上 6/47 失败（版本号与 XAML 旧值被 pin 进断言），而 CHANGELOG 明确“未运行自动测试”。
2. **F-06** `WinPool.App` 约 24,945 行（约占全库一半）无任何直接自动化测试，UI 回归依赖手工。
3. **F-07** 标准构建/清理会把 `artifacts\Release` 删除重建并强杀 WinPool 进程，而该目录同时是便携运行目录与数据根候选；审查期间实测到运行树在实例运行中被改写（10:56:40）。
4. **F-11** 产品对外无获取渠道：README 声称“当前交付为便携目录”，但仓库无 Releases、无 MSIX、无下载入口。

---

## 1. 审查方法与证据分级

### 1.1 方法

| 手段 | 说明 |
| --- | --- |
| 静态审查（4 个并行只读子代理） | ①架构与代码质量 ②安全与健壮性 ③测试与构建体系 ④产品与文档；全部只读，不运行程序 |
| 主代理亲自复核 | 对子代理的**每条关键结论**做代码定位抽查，未复核项在问题表中标注 |
| 真实构建与测试 | `dotnet build WinPool.slnx -c Release`；`dotnet test WinPool.slnx -c Release --no-build` |
| 真实软件测试 | 启动 App，用 `native-winui-control` 技能（UIAutomation + user32 P/Invoke + 截屏）逐页巡检 8 个页面，含截图与视觉分析 |
| 稳定性专项 | 5 次受控启动 + 2 秒粒度进程轮询；WER 报告与事件日志比对；强制结束对照实验 |
| 数据边界 | 未切换“真实编辑”开关；未执行任何存储命令；未删除或重建作者数据；结束时清理审查实例 |

### 1.2 证据分级（本报告统一标注）

- **【实测】**：本轮审查亲自执行并留证（命令输出、进程观察、截图、WER 文件）。
- **【代码】**：评审者本人读取源码确认位置与语义。
- **【子代理】**：子代理静态结论，评审者未逐条复核（可能含误报，按此口径引用）。
- **【跨报告】**：与同日其他独立审查（DeepSeek）结论一致或互补，用于交叉印证。

---

## 2. 静态审查

### 2.1 架构与依赖一致性 【子代理 + 主代理抽检】

实际 `ProjectReference` 方向与 `docs/Development.md` 声称的“表现与适配层 → Application → Domain”完全一致，
`WinPool.Architecture.Tests` 的 `AllowedReferences` 白名单对 7 个内层项目强制依赖方向，
`PureLayersDoNotReferenceUiDatabasePowershellOrProcessApis` 进一步禁止纯层引用 UI/SQLite/PowerShell/Process。
数据所有权（App 只读 SQLite、Agent 唯一写租约、偏好文件单写者）在代码中可验证。

发现的漂移：

- `docs/Development.md:14` 声称 Inventory / Monitoring 拥有“采集与监控契约、适配接口及数据模型”，
  但 `IInventoryProvider`、`MonitorSample` 等契约实际定义在 `WinPool.Application`；
  `WinPool.Inventory` 仅剩 `InventoryComparer.cs`（162 行），近乎空壳。
- 架构测试 45 项中绝大多数是**读源码/XAML 做字符串包含断言**，守护的是代码文本形态而非运行时行为，
  与 `docs/Quality.md` 自己写的“不以扫描源代码字符串代替行为验证”口径存在张力。

### 2.2 代码质量 【子代理 + 主代理复核】

优点（有代码依据）：只读采集链路设计扎实（固定内嵌脚本、stdin 传参、60 s 超时整树击杀、
`ReadOnlyStorageCommandPolicy` 二次校验）；`Execution` 层全面注入 `TimeProvider`、
故障注入检查点（BeforeApply/AfterApply/BeforeComplete）配合回执做失败恢复；
IPC 客户端精确区分“结果未知”与“已取消”；UI 线程纪律良好（`DispatcherQueue.TryEnqueue`）；
监控丢失语义区分“已接受未落盘/不可确认缺口”。

**P2 级缺陷（主代理已逐条复核代码）**：

- **F-15（P2）** `MainWindow.FireAndForget` 的异常白名单只捕获
  `IOException / UnauthorizedAccessException / InvalidOperationException`，
  而 `AgentBackedWorkspaceStateService` 在 Agent 响应异常时抛出 `System.IO.InvalidDataException`
  （继承自 `SystemException`，不在白名单内）——异常将从 `async void` 逸出并崩溃。
  同类白名单还出现在提权交接路径；两个执行模式开关处理器甚至没有 try/catch。
  【代码】`src/WinPool.App/MainWindow.xaml.cs:1337-1349`、
  `src/WinPool.Infrastructure.Windows/AgentBackedWorkspaceStateService.cs:36-41`
- **F-16（P2）** `MonitoringService` 的 `IsRunning / _loopCts / _remoteSessionId / LastError`
  在 UI 线程与后台轮询任务间无锁读写，`StartAsync` 的入口守卫是非原子 check-then-act；
  可造成开关状态与实际会话短暂不一致、停止后残留一轮轮询。【子代理】
- **F-17（P2）** `WindowsHardwareInventoryProvider.CollectHardwareAsync` 忽略构造函数注入的
  runner，内部自建默认实例；且两个默认 `CollectionPurpose` 互相矛盾（Hardware vs Storage）。【子代理】

**P3 级（择要）**：采集/执行失败丢弃底层异常细节（诊断能力不足，与 F-02 呼应）；
PowerShell runner 在 stdin 写入阶段被取消时不回收子进程；`pdh:`/`pdh-storage-spaces:` 前缀构成
无共享常量的跨项目隐式协议；`WorkspaceViewModel` 2,122 行 / 16 参构造、
`StorageStructurePage.xaml.cs` 3,827 行；并发扫描请求被静默丢弃（用户点刷新无反馈）；
`OperationAuthorization` 令牌字典只增不删。【子代理 + 代码】

### 2.3 安全与健壮性 【子代理 + 主代理复核】

**安全模型（从代码推断）**：App 与 Agent 同目录部署、默认普通用户运行；类型化命名管道
（ACL 仅当前用户 SID、`FirstPipeInstance` 防抢注、随机管道名 + nonce + 定长时间比较 +
时钟新鲜度 ±30 s + PID 与启动时间见证 + 客户端镜像路径校验）；18 种封闭请求类型，无“执行任意命令”消息；
SQLite 全参数化（88 处字面量 SQL、235 处参数绑定），无字符串拼接 SQL。

**优点**：内嵌 PowerShell 无注入面（编译期常量，仅前缀一个布尔开关；exe 用 System32 绝对路径，
`ArgumentList` + stdin）；文件写入普遍原子（temp + Move/Replace）；诊断日志 1 MB 上限 + 单次滚动；
未发现任何可导致真实存储结构改变的代码路径（与 `AGENTS.md` 的只读承诺一致）。

**P2 级缺陷**：

- **F-08（P2）** 提权模式下，7-Zip 自定义路径零校验：`LocalAgentPreferencesService.Normalize`
  只做空值处理（`src/WinPool.Infrastructure.Windows/WindowsServices.cs:371-379`，【代码】已复核），
  `SevenZipArchiveAdapter.ResolveExecutablePath` 仅验证“绝对路径 + 文件存在”
  （`src/WinPool.Infrastructure.Sqlite/ControlledProcessRunner.cs:496-516`，【代码】已复核），
  而偏好文件位于用户可写的 `%LOCALAPPDATA%\WinPool\agent-settings.json`。
  攻击链：同用户非提升进程改写该文件 → 提升状态的 Agent 在下一次监控归档轮换时执行指定 exe → 绕过 UAC。
  默认（非提升）部署下不跨权限边界。
- **F-09（P2）** 提升 Agent 对客户端的唯一身份校验是“镜像路径 == Agent 目录下 WinPool.App.exe”；
  便携部署位于用户可写目录时，运行中的 exe 可被改名并在原路径投放恶意镜像以通过握手，
  再配合 F-08 或 `ExportAgentMonitorCsvRequest` 扩大影响。Program Files 部署下该链断裂。
- **F-10（P2）** 控制管道 `maxNumberOfServerInstances:1` 且请求读取无空闲超时，
  同用户单个客户端即可长期独占唯一管道，使 App 全部 IPC 失联（可用性）。【跨报告：与 DeepSeek P1#4 同源】
- **F-19（P2，转述）** `ExportAgentMonitorCsvRequest` 构成任意绝对路径 `.csv` 写入原语（可配合 NTFS ADS）
  ——由同日其他审查报告提出并经其核实的调用链，**本报告未独立复核**，按【跨报告】记录。【跨报告】

**P3 级**：同用户可伪造端点投喂假数据（仅数据完整性）；App 退出事件无 ACL；
CSV 导出不处理前导 `=`/`+`/`-`/`@`（Excel 公式注入，产品决定保留原值，仅建议外发提示）；
少数 `ShellExecute` 用裸名启动系统工具（`dfrgui.exe`、`explorer.exe`）。【子代理】

---

## 3. 真实软件测试

### 3.1 构建 【实测】

```
dotnet build WinPool.slnx -c Release
→ 已成功生成。0 个警告，0 个错误。耗时 00:00:56.61
→ Union merge: 288 shared, 294 App-only, 10 Agent-only, 0 collisions
```

App 与 Agent 分别输出到 `artifacts\trees\Release\{App,Agent}`，
再由 `build\Merge-RuntimeTrees.ps1` 做 SHA-256 校验并集合并。设计意图明确、有架构测试锁住。
`TreatWarningsAsErrors` 在多数项目启用，故“0 警告”是机制保证（4 个项目未启用，见 F-05）。

### 3.2 自动化测试 【实测】

**723 项测试：714 通过 / 6 失败 / 3 跳过**（逐项目明细见证据台账第 2 节）。

- **失败集中在架构测试（6/47）**，且全部是源码/XAML 文本断言与 V0.56 现状脱节：
  `WinPoolVersionIteration>5`（实际 6）、`Width="320"`（V0.56 已改为 260 DIP）、
  `StackPanel MinWidth="412"`（V0.56 已移除）、两个已重命名的方法名。
  `docs/CHANGELOG.md` 2026-09-24 自述“未运行自动测试或原生界面验收”，与失败状态互证。
- **3 项跳过**是 1 GiB 归档测量（环境变量门控），文档口径诚实，非掩盖。
- 测试“含金量”高于数量：`WinPool.Agent.Client.Tests` 用生产版管道服务器做进程内 E2E；
  `WinPool.Persistence.Tests` 用真实 SQLite 临时库、真实 7za、可控进程注入做故障路径；
  `WinPool.Execution.Tests` 覆盖 AfterApply 故障回滚、取消、环境不匹配拒绝。
- **覆盖结构失衡**：11 个 src 项目对 11 个测试项目的关系不成立——
  `WinPool.App`（60 文件 / 24,945 行，约占全库一半）无对应测试项目，
  唯一被单测的 App 代码是源链接进来的 `MonitorIssueStateTracker.cs`。【子代理 + 实测计数】

### 3.3 原生界面实测 【实测】

启动即得欢迎窗口 + 主窗口，Agent 托盘进程同起。8 项导航全部可达并逐页巡检（设置、管理、硬件、
监控、测试、开发、存储结构编辑、磁盘分区编辑），截图 6 张（见证据目录）。

**与文档声称一致的正常行为**：

- 版本一致：设置页显示 V0.56 ↔ `Directory.Build.props`(0,5,6) ↔ README V0.56。
- 管理页拓扑与五类对比表、系统选择器（1 本机 + 5 模拟，`[本机]/[模拟]` 前缀）工作正常。
- 硬件页十段只读报告 + 导出按钮，与 `Product.md` 第 80 行声明一致。
- 监控页真实会话（“运行时长：00:05:15”）、真实设备行（HDD/SSD、容量、盘符）、颜色曲线入口齐备。
- 开发页消息列表显示本次运行 3 条消息：2 条「采集成功」（存储系统 → 完整硬件，顺序正确）
  + 1 条「监控异常」（诚实报告“上次监控异常结束：可能有数量未知的未保存样本”，
  对应被强制结束的前一实例）——缺口语义与文档一致。
- 编辑页禁用态与原因齐备；缺失值用“—”占位；测试页仅双语路线说明。
- 单实例语义正确：已有实例时启动第二实例，2 秒内以退出码 0 退出并转交激活（实测 2 次）。

**无障碍缺陷（实测）**：

- **F-13（P2）** 三类 `ListItem` 的 UIA `Name` 泄漏内部表示，屏幕阅读器会朗读：
  主导航 8 项 `WinPool.App.ViewModels.ShellNavigationItem`；
  管理页类别 5 项 `CategoryItem { Category = System, Title = 系统, Glyph = ?? }`（含乱码字形）；
  开发页消息 `SessionMessageItem { Id = …, Notification = GlobalNotification { … } }`（整对象序列化）。

**视觉观察**：管理页在 1440×900 下属性表最右列被截断且无滚动提示（F-14）；
标题栏“真实编辑”开关无就近说明；其余页面视觉一致、密度适中、主题表面统一。

### 3.4 运行期稳定性专项（本报告的核心独立贡献）【实测 + 跨报告】

**事实一：存在真实的、可重复的崩溃签名。**
`C:\ProgramData\Microsoft\Windows\WER\ReportArchive` 有 7 份 `AppCrash_WinPool.App.exe_*` 报告
（2026-09-22 → 09-24），其中 6 份签名完全一致：`combase.dll` + 异常代码 `0x80004003`（E_POINTER）
+ 偏移 `0x261d2`；**2 份（11:17:14、11:34:04）属于本次审查构建的 0.5.6.0**，
且 `Report.wer` 的 `UI[2]`/`LoadedModule[0]` 证实运行自标准 `artifacts\Release`。

**事实二：强杀与这些报告无关（对照实验）。**
本轮审查两次以 `Stop-Process -Force` 结束实例（11:05:44、11:42:23），WER 目录无对应新增。
→ 4.2 的两条 0.5.6.0 记录是真实的**进程内故障**，不是结束手段的产物。

**事实三：普通交互不触发（受控存活实验）。**
3 轮受控启动（含 2 轮完整 UIA 导航 + 截图 + 前台切换）各自存活 ≥ 300 秒。
唯一“秒退”案例（2 秒、退出码 0）是单实例激活转发，属预期行为。

**事实四：触发场景指向“运行中的便携目录被判构建替换”。**

- `artifacts/Release/WinPool.App.{exe,dll}` 时间戳为 **10:56:40**，而审查期第一个实例约 10:53 启动、
  10:56–10:57 消失——运行树在实例运行期间被改写，时间窗完全重合。
- `artifacts/test-results/` 下存在同期其他自动审查代理的产物
  （`GPT-review-20260924` 10:57:07、`grok-independent-review-20260924` 11:18:41），
  其测试/构建动作会写入并替换 `artifacts\Release`；用户亦确认当时“有多个 Agent 抢夺真实软件控制”。
- `build\Clean-WinPool.ps1` 会强制结束 WinPool 进程；标准重建流程的产物目录与便携运行目录同一。

**事实五：App 自有崩溃日志对这类故障失明。**
`%LocalAppData%\WinPool\Diagnostics\app-crash.jsonl` 仅 1 条（2026-09-23，`XamlUnhandled` /
`XamlParseException`），对应 WER 09-23 20:17:02；其余 6 条（含 2 条本机构建版本）无任何自有记录。
代码依据【代码】：`App.xaml.cs:64-67` 确实注册了三个处理器
（`UnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`），
但 `App_UnhandledException`（96-109 行）只写日志、**未设置 `e.Handled = true`**；
COM/原生访问冲突不经过托管 `AppDomain` 异常通道，因此不落盘。

**结论与处置建议**：这不是“普通用户随时会遇到的崩溃”，而是**“构建/清理/多实例并存时替换运行树”
这一工作流耦合触发的故障**，但它是真实存在的进程内故障，且（a）产品自己的诊断目录对用户与开发者
都无信息，（b）README 对“替换程序文件前退出 WinPool”仅有一行文字警告，没有机制化保障。建议：

1. 注册 WER `LocalDumps`（或 `AppDomain`+`AppDomain.CurrentDomain.FirstChanceException` 组合）
   拿到一次完整调用栈，用 BucketId `575b8f42…` 与偏移 `0x261d2` 作为回归基线；
2. 让构建/清理脚本在替换 `artifacts\Release` 前检测目标 EXE 是否被占用（占用即失败退出，
   而不是先 `Remove-Item` 再 `Move-Item`），或把运行树与构建产物目录彻底分离；
3. 可恢复场景设置 `e.Handled = true` 并给出用户可见错误，不可恢复场景至少保证落盘。

> 与同日其他独立审查（DeepSeek）的交叉验证：对方以 WER 归档 + 事件日志独立得出“7 天内 6 次同一签名
> 崩溃、自有日志只记 1 条”的结论（其 P1#10），与本报告事实一、五一致；本报告补充了两项对方没有的
> 对照证据（**强杀不产生 WER 报告**、**受控环境 3×300 秒无异常**），因此对触发场景的归因更收敛：
> 主因是运行树被替换 + 并发操作，而不是常规交互路径。对方 P1#2（构建会删除并重建便携数据根目录）
> 与本报告 F-07 属同一问题的两个侧面。

### 3.5 本轮未验证项（诚实声明）

- **UAC 提升全流程**未实测（需交互式 UAC 授权，且属真实系统边界，非本报告授权范围）。
- **开发者模式“默认关闭”**未在全新数据状态验证（审查保留作者现有开发数据，实例继承了开发者模式已开启的状态）。
- **真实 1 GiB 监控轮换归档**未复测（同日另一份审查已用 `7za t` 校验既有归档产物完整性）。
- **Windows 11 / 多语言 / 高对比度 / 主题全矩阵**未覆盖（本机为 Windows 10 22H2，与产品最低支持系统一致）。
- **真实存储写操作**按产品边界与仓库规则一律未触发。
- 审查期间环境存在多个自动代理并发操作同一 App 与构建产物（用户确认），
  运行期证据已按“交互窗口”分层采信，受影响时段以受控实验为准。

---

## 4. 产品设计评价

### 4.1 定位与用户

`Product.md` 以“存储结构管理软件”定位，README 用“查看存储拓扑、监控设备和编辑模拟存储系统”。
当前版本无任何真实写操作，"管理"一词超前于能力；README 表述更诚实。
从文档可辨识的目标用户：第一用户是作者本人（Plan 绑定具体机器/磁盘、内置复刻研究平台的模拟系统、
面向 AI 代理的 AGENTS 规则）；第二用户是 Windows 存储爱好者 / homelab 用户，
需要一个“先在模拟环境排练、再理解本机事实”的低风险环境。企业存储管理员不在其中（无许可证、无支持承诺）。

**价值主张清晰且差异化**：“以研究证据为底的安全排练场”——只读采集真实事实 + 受规则约束的模拟编辑
+ 长周期监控与 1 GiB 轮换归档，配合 64K+64K 研究结论形成独特权威性。
`Product.md` “不以界面能画出来作为配置合法性的依据”是很好的反承诺。

### 4.2 交互与信息架构

读写分离（管理页只读事实 / 编辑页模拟草稿）、两种提交模型分页承载
（结构页净差异草稿 vs 分区页即时提交）是合理架构。标题栏全局系统选择器承担“本机 vs 模拟”身份，
`[本机]/[模拟]` 前缀已实测生效。

**风险（按优先级）**：

1. **模拟/真实混淆风险**：当 V0.5x 把真实执行接入同一批编辑页后，同一按钮在两种系统下语义分叉，
   将成为最高风险交互点。当前“真实编辑”开关仅四字标签、无就近说明（F-14 的一部分），
   建议配持久全局模式视觉与不可混淆的预览措辞。
2. **导出隐私**：导出含序列号/MAC/卷 GUID 原值且无提示（F-12）；用户排障时分享导出文件是
   高概率行为。产品“保持原值、不脱敏”是明确决定，但**外发面**应加一次性提示。
3. **告警疲劳残余**：独立重复消息不合并、最多 3 卡、消息列表仅内存退出即清，
   级联故障时用户既看不全也无法回看。
4. **监控归档不可见增长**：归档永不自动删除、软件不提供历史查看与清理入口，用户无体量感知。
5. **调性断裂**：欢迎窗口随机轮播表情包素材（`assets/welcome/`，约 13 MB，随产品分发，无来源/授权说明），
   与严肃存储工具的定位冲突（F-12 附带的产品问题）。

### 4.3 信任与安全承诺的一致性（亮点）

- “不含任何真实存储写操作”经静态审查确认为**属实**（真实写入路径不可达；本报告与另一份独立审查均确认）。
- 授权三分（开发者每次批准 / 用户当次显式选择 / UAC 与 Real 模式不构成授权）设计严谨，
  已在 `Product.md` 与 `Plan.md`（含目标盘白名单）中预演到位。
- 失败语义诚实：采集失败保留旧数据、历史加载不冒充采集成功、提交结果未知单独归类；
  监控缺口如实显示（实测到该警告）。
- 研究结论措辞（64K+64K）在 README 双语与 `Product.md` 中一致且完整。

### 4.4 文档体系

**优点**：职责单一（Product/Development/Quality/Plan/CHANGELOG 各管一域）；
`Quality.md` 主动记录未验证项与失败复核（“截图全黑，不能作为视觉证据”等），可信度资产；
`docs/Archive/` 索引逐条标注真实状态（含 superseded / invalid）；归档纪律高于同类项目。

**问题**：双语 README 已出现漂移（英文版缺“开发者模式默认关闭”、缺开发页暂留空说明、
十段报告未列名、段落结构不同）；CHANGELOG 把用户可感知变更与内部验证台账（TRX 路径、DIP 数值）
混排，外部用户无法快速得知版本差异；README 邀请贡献者（"Contributor instructions start at AGENTS"）
但仓库无 LICENSE 且 AGENTS 明确排除 PR 流程，指引实质落空。

---

## 5. 问题汇总

级别定义：**P1** 阻断交付/信任或真实故障；**P2** 明显损害质量、安全或体验；**P3** 建议改进。
证据类型：【实测】本次执行；【代码】本人读码；【子代理】未逐条复核；【跨报告】其他独立审查。

| # | 级别 | 问题 | 关键证据 | 类型 |
| --- | --- | --- | --- | --- |
| F-01 | P1 | 自动测试门在 HEAD 上为红：`Architecture.Tests` 6/47 失败（版本号与 XAML 旧值 pin 进断言），CHANGELOG 声明提版未跑测试 | TRX 实测；`ArchitectureBoundaryTests.cs:547/866/1013/1171/1408` | 质量门 |
| F-02 | P2 | 架构测试 45 项几乎全为源码/XAML 文本断言，高维护摩擦且与 Quality.md 口径冲突 | 【子代理】+命名清单实测 | 测试 |
| F-03 | P2 | 架构依赖白名单只覆盖 7/11 个项目 | 【子代理】 | 测试 |
| F-04 | P2 | 提升链路（runas/双事件握手/超时）仅 1 个 SID 哈希测试；`DesktopAgentRuntime`、`TrayApplicationContext` 无测试 | 【子代理】 | 测试 |
| F-05 | P3 | 无 CI；11 个测试项目重复包版本；4 个项目缺 `TreatWarningsAsErrors`；`Infrastructure.Tests` 真实机器测试无 trait 门控 | 【子代理】 | 工程 |
| F-06 | P1 | `WinPool.App`（60 文件/24,945 行，约占全库一半）无直接自动化测试 | 【子代理】+实测计数 | 测试 |
| F-07 | P1 | 标准构建/清理会删除重建 `artifacts\Release`（=便携运行目录=数据根候选）并强杀 WinPool 进程；实测运行树在实例运行中被改写（10:56:40） | 【实测】时间戳 + `Clean-WinPool.ps1` 内容 + 【跨报告】P1#2 | 流程/数据安全 |
| F-08 | P2 | 提权模式下 7-Zip 自定义路径零校验 → 用户可写配置可令提升 Agent 执行任意 exe（UAC 绕过链） | 【代码】`WindowsServices.cs:371-379`、`ControlledProcessRunner.cs:496-516` | 安全 |
| F-09 | P2 | 提升 Agent 客户端身份仅靠镜像路径，便携可写目录下可伪造（与 F-08 可组合） | 【子代理】 | 安全 |
| F-10 | P2 | 控制管道单实例 + 帧读取无空闲超时 → 同用户可独占管道使 App 全部 IPC 失联 | 【跨报告】DeepSeek P1#4 同源 | 可用性 |
| F-11 | P1 | 产品对外无获取渠道：README 称“当前交付为便携目录”，但无 Releases/下载入口/截图 | 【子代理】（README 全文核对） | 产品/交付 |
| F-12 | P2 | 导出含硬件序列号/MAC 原值且无隐私提示；欢迎窗口表情包素材来源与授权不明（约 13 MB 随产品分发） | 【子代理】 | 合规/信任 |
| F-13 | P2 | 主导航/类别列表/消息列表的 UIA `Name` 泄漏内部类型名与 record 序列化（含乱码），屏幕阅读器体验受损 | 【实测】UIA 转储 | 无障碍 |
| F-14 | P3 | 管理页属性表右列在 1440 宽被截断且无滚动提示；标题栏“真实编辑”开关无就近说明 | 【实测】截图 + 视觉分析 | 界面 |
| F-15 | P2 | `async void` 异常白名单漏 `InvalidDataException` → 真实崩溃链（Agent 不可用窗口期） | 【代码】`MainWindow.xaml.cs:1337`、`AgentBackedWorkspaceStateService.cs:36-41` | 代码质量 |
| F-16 | P2 | `MonitoringService` 跨线程共享状态无同步（开关状态毛刺、停止后残留轮询） | 【子代理】 | 并发 |
| F-17 | P2 | `CollectHardwareAsync` 绕过注入 runner，默认 `CollectionPurpose` 互相矛盾 | 【子代理】 | DI/维护 |
| F-18 | P2 | 崩溃可观测性盲区：7 次同一签名崩溃，自有日志只记录 1 类；XAML 处理器不设 `Handled` | 【实测】WER + jsonl + 【代码】`App.xaml.cs:96-109` | 稳定性/可观测 |
| F-19 | P2 | `ExportAgentMonitorCsvRequest` 构成任意绝对路径 `.csv` 写入原语（可配合 ADS） | 【跨报告】DeepSeek P1#3，本报告未复核 | 安全 |
| F-20 | P2 | 双语 README 漂移（4 处）；CHANGELOG 缺用户视角摘要；无 LICENSE 却邀请贡献者 | 【子代理】 | 文档 |
| F-21 | P3 | 采集/执行失败丢弃底层异常细节；PowerShell runner 取消时不回收子进程；`pdh:` 隐式跨项目协议；超大 VM/编辑页；并发扫描请求静默丢弃；授权令牌字典无回收；`Inventory` 近乎空壳；`Product.md` 定位超前；README 首屏混入内部格式号；监控归档无可见性；仓库卫生（`.lnk`、`temp/`、`skills/`、散置文本）；AGENTS.md 携带过期 V0.49 版本号 | 见第 2 章各条 | 维护性 |

---

## 6. 优势清单（有证据支撑，非客套）

1. **“不含真实存储写操作”经得起验证**：静态审查未找到真实写入可达路径，与 README/Product 声明一致。
2. **只读采集链路设计扎实**：固定内嵌脚本、无注入面、超时整树击杀、denylist 二次校验、失败显式报错。
3. **IPC 纵深防御完整**：ACL + 单实例 + 随机名 + nonce + 定长时间比较 + PID 见证 + 镜像校验 + 帧上限。
4. **构建体系严谨**：单一版本源、输出集中 `artifacts/`、SHA-256 并集合并 + 碰撞即失败、0 警告机制保证。
5. **测试含金量高**：714 项通过，含真实命名管道 E2E、真实 SQLite 迁移/轮换/归档、真实 7za、
   故障注入与回滚；1 GiB 测量按环境变量门控并诚实标注跳过。
6. **失败语义与监控缺口诚实**：采集失败保留旧数据、历史不冒充新采集、监控异常如实提示
   （本轮实测到该行为），这在存储类工具中尤其重要。
7. **文档纪律罕见**：`Quality.md` 记录未验证项与失败复核、`Archive/` 逐条标注真实状态、
   CHANGELOG 记录自己的“未运行测试/旧树未替换”等不利事实。
8. **原生界面可用性良好**：8 页全部可达且行为与文档一致，禁用态原因齐备，单实例语义正确，
   键盘/主题/双语基础设施在设置页实测可用。

---

## 7. 结论与建议行动

**结论**：WinPool 的工程内核与其对外宣称高度一致——“只读、诚实、可追溯”不是文案，而是可验证的实现。
当前状态的真正风险不在功能欺骗，而在三处“门与边界”：**质量门已失效**（架构测试红、无 CI、
App 层无测试）、**运行环境边界未隔离**（构建/清理直接改写运行中的便携目录，产生真实崩溃且无法自诊断）、
**交付边界未闭环**（无获取渠道、导出无隐私提示、素材授权不明）。以 V0.56 的阶段定位衡量，
这些属于“进入 1.0 前必须收口”的项，而不是设计缺陷。

**建议行动顺序**：

1. **恢复门的可信度（1 天级）**：修 F-01 的 6 项断言——把版本号断言改为**解析 `Directory.Build.props`
   并校验派生一致性**（不再 pin 字面量），把 XAML 尺寸断言改为结构化或删除；
   在提版流程中把“全量 Architecture 回归”列为必跑项。
2. **隔离运行树与构建产物（1–2 天）**：构建/清理脚本在替换前检测目标 EXE 占用并快速失败；
   或把便携运行树移出构建输出目录（F-07）。同时注册 WER `LocalDumps`，拿到一次 `0x261d2` 调用栈（F-18）。
3. **收口安全问题**：提升模式禁用自定义 7-Zip 路径（或用签名/白名单校验）修 F-08；
   给控制管道帧读取加空闲超时修 F-10；复核 F-19 并决定是否限制导出路径。
4. **修 async void 白名单与并发状态（1 天级）**：F-15、F-16、F-17。
5. **补 App 层最小回归**：以 UIA 冒烟（导航门、通知、两编辑页提交链）覆盖 F-06 的高价值路径，
   同时把可测逻辑持续下沉到 Application 层（已有 `MonitorIssueStateTracker` 先例）。
6. **产品与合规收口（1.0 前必须）**：README 增加获取渠道或明示“仅源码/自构建”（F-11）；
   导出按钮加隐私提示、欢迎素材补来源/授权或替换（F-12）；逐段对齐双语 README（F-20）。
7. **无障碍修复（低成本高收益）**：为导航项、类别项、消息项设置显式 `AutomationProperties.Name`
   或改用只读展示模型（F-13）。

---

## 附录 A：审查执行记录

```text
# 构建与测试
dotnet --version                                    → 10.0.401
dotnet build WinPool.slnx -c Release                → 0 警告 0 错误，56.61 s
dotnet test WinPool.slnx -c Release --no-build      → 714 通过 / 6 失败 / 3 跳过（723）
dotnet test tests/WinPool.Architecture.Tests …      → 失败 6，通过 41（逐条错误消息已记录）

# 原生界面（native-winui-control：UIAutomation + user32 P/Invoke + System.Drawing）
temp/ZcodeReview/uia-status.ps1                     → 进程/窗口发现
temp/ZcodeReview/uia-dump.ps1                       → 无障碍树转储 + 截图
temp/ZcodeReview/uia-nav.ps1                        → 按显示文本导航 + 页面转储 + 截图
temp/ZcodeReview/survival-test.ps1                  → 2 s 粒度进程存活轮询（5 分钟）
temp/ZcodeReview/survival-interactive.ps1           → 空闲 45 s → UIA 导航 → 观察至 300 s

# 崩溃取证
Get-ChildItem C:\ProgramData\Microsoft\Windows\WER\ReportArchive | …WinPool*  → 7 份 AppCrash
Get-Content …\AppCrash_WinPool.App.exe_c2349d91…\Report.wer                    → 0.5.6.0 / combase / 0x80004003 / 0x261d2
Get-Content %LocalAppData%\WinPool\Diagnostics\app-crash.jsonl                 → 仅 1 条（XamlUnhandled）
ls --time-style=full-iso artifacts/Release/WinPool.App.{exe,dll}               → 2026-09-24 10:56:40（运行期被改写）
```

**证据文件**：`docs/Review/Zcode-Independent-Review-20260924-evidence/`
（`evidence-ledger.md` 证据台账含测试矩阵、WER 明细、UIA 观察原文；
`shot-02`…`shot-07` 六张页面截图；`interactive-log.txt` 存活实验原始日志）。

## 附录 B：与其他独立审查的交叉验证

同日仓库内另有 `DeepSeek-Independent-Review-20260924.md`。两份报告独立取证，结论对照如下：

| 议题 | 一致性 | 说明 |
| --- | --- | --- |
| 真实写操作不可达 | 双方一致 | 双方均以不同路径确认产品声明属实 |
| 架构测试门在 HEAD 上失败 | 双方一致 | 对方记录 6/47 失败，本报告实测复现并给出逐条错误消息 |
| `combase.dll` E_POINTER 崩溃签名 | 双方一致、本报告细化了归因 | 对方以 WER/事件日志列出 7 次；本报告补充“强杀对照实验不产生 WER”“受控 3×300 s 无异常”“运行树 10:56:40 被改写”，将主因收敛到运行树替换 + 并发操作 |
| 构建删除便携数据根 | 双方一致 | 对方列为 P1#2；本报告列为 F-07，并补充运行期时间戳证据 |
| 导航 UIA 名称泄漏类型名 | 双方一致 | 对方列 P1#7；本报告实测复现并扩展到类别列表与消息列表 |
| 控制管道可被同用户独占 | 双方一致 | 对方 P1#4；本报告 F-10 按未复核口径引用 |
| CSV 任意路径写入、数据根迁移 WAL 风险、FixedWrapPanel 布局、错误卡死胡同 | 仅对方提出 | 本报告未复核，建议作者以对方给出的调用链直接核对 |
| App 崩溃“自有日志只覆盖 XAML” | 部分不一致（措辞） | 本报告核对代码：三个处理器均已注册；不落盘的原因是 COM/原生故障不经托管通道且 XAML 处理器未设 `Handled` |

> 说明：审查期间环境存在多代理并发（用户确认）。本报告对运行期证据采用“受控实验优先”的采信顺序，
> 因此对崩溃触发条件的结论比单一来源更收敛；两份报告中互相冲突的措辞以代码与原始日志为准。

---

*本报告由 Zcode 以独立审查官角色出具；全程只读、未修改产品代码、未触发真实存储操作、
未切换真实编辑开关；审查实例已在结束时关闭。*
