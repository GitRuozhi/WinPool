# WinPool V0.44 平台升级与发行瘦身计划

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

> 本文件仅为中文阅读副本；计划权威以无 `.zh-CN` 后缀的
> [Plan.md](Plan.md) 为准。

## 0. 状态、授权与基线

- **计划状态：** 范围已确认，尚未开始实施
- **创建日期：** 2026-09-01
- **基线提交：** `407c1e92c1493dd41b608d0b5693715ffd22382e`
- **工作分支：** `main`
- **当前产品版本：** V0.43
- **目标产品版本：** V0.44
- **阶段性质：** 平台现代化与 portable 发行瘦身；不新增用户功能

本计划之前的中文手写草案已冻结在
[`Archive/V0.44-draft`](Archive/V0.44-draft/README.md)。它们只是历史输入。
本文件是唯一的活动 Plan。

开发者已经确认以下控制决定：

1. V0.44 将 Windows App SDK 从当前 1.8 系列升级到 **2.4 Stable**。当前稳定包是
   **2.4.0**。实施时若已有 2.4.x 服务更新，可以使用。预览版和实验版不在范围。
2. V0.44 将 Windows SDK 工具从当前 26100 系列推向 **28000 系列**，但须服从 WP1
   的 TFM 探测。
3. .NET 继续使用 **.NET 10**。本阶段不更换 .NET 主版本，也不为了追 Windows TFM
   而放宽 `global.json`。
4. 对外公开的最低 Windows 版本调整为 **Windows 10 22H2 x64**。
5. 更早 Windows 版本可以做非承诺性兼容检查，但不属于公开支持矩阵，也不能阻塞
   V0.44。
6. Windows 11 24H2 与 25H2 为点名的主要平台。仍受支持的更新 Windows 11 版本可在
   验证后补充。
7. V0.44 继续只提供当前的 **unpackaged、portable、完全 self-contained x64**
   发行模式。
8. framework-dependent、Lite、single-file、MSIX、ARM64 和 x86 发行属于后续产品
   计划，不在 V0.44。
9. `WinPool.App` 与 `WinPool.Agent` 继续保持两个进程。首选 portable 树只保存一份
   公共 self-contained runtime 文件。
10. Windows App SDK 必须按 WinPool 实际使用的组件引用。未使用的 AI、ML、ONNX、
    DirectML、Widgets、Search 等载荷不得只因引用顶层元包而进入 portable 树。
11. Agent 继续使用现有 WinForms 托盘。V0.44 不实施 `Shell_NotifyIcon` 替换。
12. 欢迎图片和其他现有图片字节不做压缩、降分辨率、重新编码或格式转换。
13. 正式 staging 和发行 ZIP 不包含 PDB。构建和诊断产物继续保留 PDB。
14. V0.44 不启用 .NET trimming，也不把 trimming 当作实验性完成项。
15. V0.44 不设置绝对 MiB、百分比或其他硬体积门槛。体积必须记录，是否可接受由
    开发者判断。
16. V0.44 不新增用户功能、不拆分大型业务模块、不开放真实存储结构变更。
17. 在钉死的 .NET SDK 上尝试 `net10.0-windows10.0.28000.0`。若该 TFM 被拒绝
    （包括 NETSDK1140），V0.44 保留钉死 SDK 所接受的最高 Windows TFM（当前预期
    仍是 26100 系列），并且仍可将 `Microsoft.Windows.SDK.BuildTools` 更新到
    restore 接受的 28000 系列包。TFM 未能改到 28000 本身不是 V0.44 停止条件。
18. 在合并 App 与 Agent 输出之前，把面向 Windows 的工程统一到同一个 Windows
    TFM。当前分裂（App `26100`，Agent 及其他 Windows 工程 `19041`）不得带进合并
    步骤。
19. 本地 `artifacts\$(Configuration)\` 运行树与 portable staging 使用同一进程
    布局。V0.44 不为本地运行保留 nested Agent 路径、又为 staging 使用扁平路径。
20. 将 App 和 Agent 放到同一目录是首选体积结果。若同名文件内容不同，且无法在
    不合进程、不开 trimming、不删除 WinForms 的前提下调和，则可保留 nested
    `Agent\` 布局，V0.44 仍可凭平台升级、组件化和 PDB 剥离关闭。
21. 产品版本元数据在平台升级稳定之后再改为 V0.44，而不是作为第一笔代码变更。
22. `TargetPlatformMinVersion` 保持 `10.0.17763.0`，除非 Windows App SDK 2.4
    自身要求更高值。V0.44 不为了拒绝更旧 Windows 而额外加运行时版本锁。

编写本计划不等于授权实施、push、tag、GitHub Release、二进制上传、部署或真实
存储修改。只有开发者明确要求执行本计划后才开始实施。

## 1. 目标与必达结果

V0.43 仍是产品能力基线：拓扑、仿真编辑、监控、设置、托盘 Agent、IPC 协议 4 和
SQLite schema 14。V0.44 保留这些能力，并降低第三方运行时成本。

阶段完成时必须同时满足：

1. 产品版本源为 **V0.44 / 0.4.4**。
2. Windows App SDK 为 **2.4 Stable**，活动依赖图中不再有 1.8 包。
3. Windows SDK 工具处于已记录、一致的基线：TFM 探测成功则为 28000 系列，否则为
   文档化的回退。
4. .NET 仍为 .NET 10，且 `PublishTrimmed=false`。
5. 公开支持文本以 **Windows 10 22H2 x64** 为最低受支持版本，而不是“低于 build
   28000 就不能启动”。
6. App 和 Agent 仍为独立可执行文件。
7. portable 树仍无需预装 .NET Runtime 或 Windows App Runtime。
8. 未使用的 Windows App SDK AI/ML 类组件在 NuGet 图和 staging 树中都不存在。
9. 公共 self-contained runtime 文件只保存一份；或按决定 20 明确保留 nested
   布局，并记录重复体积证据。
10. Agent WinForms 托盘行为未改变。
11. 现有图片字节未改变。
12. Release staging 不含 `.pdb`。
13. 构建诊断符号仍可用。
14. WinUI XAML、PRI、XBF、启动、导航、主题、语言、Picker、Agent、IPC、盘点、
    监控和 SQLite 行为无已知 V0.44 回归。
15. 真实存储结构变更继续拒绝。

体积下降是证据，不能代替正确的依赖图或 portable self-contained 行为。

## 2. 永久安全与产品边界

V0.44 不改变 V0.43 的存储安全模型。

- 真实磁盘、分区、卷、Storage Pool、Storage Tier 和 Virtual Disk 修改继续默认
  拒绝。本阶段不实现 V0.5 操作。
- 存储编辑仍只有仿真执行路径。
- 盘点与监控对存储结构保持只读。
- Agent 仍是 SQLite 的唯一正常写入者。
- App 与 Agent 继续使用现有受约束 IPC。
- 不因为新 SDK 暴露了能力就引入自由命令、脚本、插件或公共自动化入口。
- 仅存在于比 22H2 更新的 Windows 上的 API 必须有明确版本检测和 fallback，否则
  排除在 V0.44 之外。
- 平台升级不得削弱 IPC 身份验证、管道 ACL、进程身份校验或协议边界。
- 平台升级不得改变 SQLite 所有权和写入者规则。

Windows SDK 版本是编译时 API 上限，不是对外公布的最低操作系统。

## 3. 平台基线

### 3.1 .NET

```text
.NET 10
RuntimeIdentifier: win-x64
```

本阶段不进行 .NET 主版本升级、NativeAOT、trimming、single-file 转换或
ReadyToRun 实验。`PublishTrimmed=False` 继续保留。

### 3.2 Windows SDK

基线提交时的事实：

- App TFM：`net10.0-windows10.0.26100.0`
- Agent 及其他 Windows 工程：`net10.0-windows10.0.19041.0`
- `Microsoft.Windows.SDK.BuildTools`：`10.0.26100.7705`
- 编写本计划时开发机已安装 Windows SDK `10.0.26100.0`，.NET SDK 为
  `global.json` 中的 `10.0.400`
- 部分 .NET 10 SDK 构建会拒绝 `10.0.28000.0` TFM（NETSDK1140）

因此 WP1 在提交 28000 TFM 之前先探测：

1. 在钉死的 SDK 上，用 `net10.0-windows10.0.28000.0` restore/build 一个面向
   Windows 的工程。
2. 若成功，把 WinPool 中面向 Windows 的工程统一到该 TFM，并把 BuildTools 更新到
   匹配的 28000 系列包。
3. 若失败，保留 SDK 接受的最高 Windows TFM，把 Agent 及其他 Windows 工程统一到
   同一 TFM，并在阶段记录中写下回退。若 restore 接受，BuildTools 仍可升到
   28000。
4. 不为了强迫支持 28000 TFM 而解开 `global.json`（`10.0.400`，
   `rollForward: latestPatch`）。

用更新的 SDK 编译，不等于授权在 Windows 10 22H2 上调用仅 28000 才有的 API。

### 3.3 Windows App SDK

当前包：`Microsoft.WindowsAppSDK` `1.8.260416003`。

V0.44 目标：**2.4 Stable**。Windows App SDK 1.8 维护期到 2026-09-09；该日期说明
紧迫性，不是跳过门槛的许可。

2.4 的第一步保持功能完整的依赖集合，并恢复现有行为。组件化属于后续工作包。

升级后需要确认：

- App 启动
- XAML 初始化
- PRI/XBF 加载
- Windowing
- Folder/File Picker
- 主题与强调色
- 运行时语言切换
- DPI 与基础多显示器行为
- Agent 启动和关闭
- App-Agent IPC
- unpackaged self-contained publish

`CommunityToolkit.WinUI.Controls.Sizers` 是已知的第三方风险。若它在 2.4 上失败，
使用兼容的 toolkit 版本或做最小本地修复。不得为了让升级看起来更“瘦”而删除管理页
分割条。

现有把生成的 XBF/PRI 拷入 publish 目录的 App 发布 workaround 应保留，或替换为
2.4 等价修复。portable 树仍必须包含这些资源。

## 4. Windows 支持策略

V0.44 之后的公开支持：

```text
最低受支持：Windows 10 22H2 x64
主要平台：  Windows 11 24H2 x64
            Windows 11 25H2 x64
```

V0.44 不提供 ARM64 或 x86 发行。

更旧 Windows：

- 可以启动；
- 以后可以低成本检查；
- 不属于公开保证；
- 不要求完整回归农场；
- 默认不阻塞 V0.44。

文档应使用 **最低受支持版本**，而不是该程序在此版本以下技术上绝对无法运行。

原生验证使用开发者实际拥有的机器。缺少的 SKU 记为 `unverified`，不得记为
`passed`。当 22H2 机器可用时，编译成功不能代替 Windows 10 22H2 原生冒烟。

## 5. Windows App SDK 组件化

顶层 `Microsoft.WindowsAppSDK` 2.4.0 元包当前至少会带上 Base、Foundation、
Runtime、WinUI、InteractiveExperiences、DWrite、AI、ML、Search 和 Widgets。
V0.44 不得只因为元包方便就继续携带无消费者载荷。

最小受支持集合必须根据 2.4 的 NuGet 图、编译引用、发布文件来源和干净 staging
启动来确定。本计划不提前冻结未经该图验证的包名。

移除只能通过项目/NuGet 图完成。手工删除 publish DLL 不算组件化。

预计需要审计、且 WinPool 当前没有消费者的目标：

- AI
- ML
- ONNX Runtime
- DirectML
- Widgets
- Search
- 其他没有编译或运行时消费者的可选元包组件

WinUI 仍可能把 Foundation、Base、Runtime、InteractiveExperiences 或 DWrite 当作
传递依赖。若依赖图和启动证据需要它们，就保留。

组件化完成条件：restore、build、tests 和 publish 成功；App 能从干净 staging
目录启动；WinUI/XAML 和 Picker 正常；未使用的 AI/ML 类包不在依赖图和 staging
中；并且该树不依赖开发机上全局安装的 Windows App Runtime。

## 6. App / Agent 发行布局

### 6.1 进程模型

```text
WinPool.App.exe
    WinUI 外壳、导航、页面和用户交互

WinPool.Agent.exe
    托盘运行时、盘点、监控、SQLite 单写入、
    App IPC、持久化和生命周期所有权
```

不得为了减小体积而把 Agent 合并进 App、把监控移回 App、放弃托盘生命周期，或削弱
崩溃隔离与 IPC 边界。

### 6.2 当前布局

V0.43 staging 为：

```text
WinPool.App.exe
Agent/WinPool.Agent.exe
```

这条 nested 路径目前是契约，不只是脚本细节。至少编码在：

- `Directory.Build.props`
- `src/WinPool.App/WinPool.App.csproj`
- `src/WinPool.App/App.xaml.cs`
- `src/WinPool.App/Services/AgentStartupRegistration.cs`
- `build/Publish-Staged.ps1`
- `build/Rebuild-WinPool.ps1`
- `tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs`
- `docs/Development.md`
- `docs/Quality.md`

若进行扁平化，WP4 必须一并更新上述全部位置。

### 6.3 V0.44 首选布局

```text
WinPool/
├── WinPool.App.exe
├── WinPool.App.dll
├── WinPool.App.deps.json
├── WinPool.App.runtimeconfig.json
├── WinPool.Agent.exe
├── WinPool.Agent.dll
├── WinPool.Agent.deps.json
├── WinPool.Agent.runtimeconfig.json
├── 公共 .NET、WinPool、WinUI、Windows App SDK 和 WinForms 文件
├── Assets/
└── XBF / PRI / 其他必需资源
```

每个进程继续拥有自己的 apphost、deps、runtimeconfig、入口程序集和生命周期。
公共文件在磁盘上只保存一份。两个 CLR 实例不在内存中合并。

本地 `artifacts\$(Configuration)\` 使用同一布局。

### 6.4 合并规则

不得 publish App、publish Agent，再按最后复制者覆盖。

staging 合并必须：

1. 把 App 和 Agent 分别 publish 到临时目录。
2. 对同名文件计算哈希。
3. 内容完全相同：staging 中只保留一份。
4. 内容不同：staging 构建失败。不允许静默覆盖。
5. Agent 独有的 WinForms 文件保留。
6. App 独有的 WinUI / Windows App SDK 文件保留。
7. staging 必须从空目录重建。
8. App 和 Agent 都从该合并目录启动验证。

这一步去除的是重复文件，不是让两个进程共享内存中的 Runtime。

### 6.5 扁平化回退

若 WP4 遇到无法调和的同名不同内容文件，停止扁平化。不得合进程、启用 trimming、
删除功能或改图片来强迫变瘦。保留 nested `Agent\` 布局，记录冲突文件，并继续
WP5–WP7。

## 7. PDB 与符号

继续生成符号。构建树可以包含 `*.pdb`。

正式 portable staging 和发行 ZIP 不得包含 `*.pdb`。剥离发生在 staging 规则中，
而不是关闭编译符号。

检查：

- 构建产物中仍有预期 PDB；
- staging 中没有 PDB；
- 剥离 PDB 不会误删 `.dll`、`.json`、`.pri` 或 `.xbf`。

## 8. 明确非目标

V0.44 不包含：

- 新增用户功能
- 真实存储结构变更
- V0.5 管理操作
- WorkspaceViewModel 或业务架构的大型拆分
- 原生 WinForms 托盘替换
- 图片压缩或格式转换
- 删除或更改欢迎图选择逻辑
- trimming、NativeAOT、single-file 或 ReadyToRun 实验
- framework-dependent 或 Lite 发行
- 新的 MSIX 模式
- ARM64 或 x86
- 自动更新或安装器设计
- V1.0 多渠道发行设计

若上述事项也能减小体积，只能记为后续候选，不得扩大 V0.44 范围去实施。

## 9. 工作包与顺序

WP0–WP7 只有在开发者明确授权后才开始实施。

### WP0 — 冻结并测量 V0.43 基线

任何升级之前：

1. 记录基线提交。
2. 从干净工作树执行 [Quality](Quality.md) 中的标准 restore/test/build。
3. 用 `build/Publish-Staged.ps1` 把 V0.43 Release portable staging 生成到新路径。
4. 记录总大小、文件数、App 大小、Agent 大小、App/Agent 重复文件大小、PDB 大小、
   Windows App SDK 相关大小、.NET runtime 大小、WinForms 大小和 WinPool 自有
   大小。
5. 保存文本报告。不提交大型临时二进制。

手写草案提到完整 V0.43 可分发树约 380.44 MiB。编写本计划时本地
`artifacts\Release` 是另一棵树（约 294 MiB），不能当作那次测量。WP0 必须从本
基线提交重新测量。手写数字不得再当证据使用。

基线检查失败必须记为 `failed` 或 `unverified`，不能写成 V0.43 已通过状态。

### WP1 — Windows SDK 探测与 TFM 统一

1. 在钉死的 .NET SDK 上探测 `net10.0-windows10.0.28000.0`。
2. 采用 28000 TFM 或文档化回退。
3. 把面向 Windows 的工程统一到同一个 Windows TFM。
4. 更新 BuildTools、projection，以及测试中硬编码的平台版本假设。
5. 修复编译错误。不主动采用仅 28000 才有的产品 API。
6. restore、build 和 test。

### WP2 — Windows App SDK 2.4 功能等价

1. 将 `Microsoft.WindowsAppSDK` 升到 2.4 Stable，本步保持功能完整的依赖集合。
2. 修复 breaking API、WinUI、XAML 资源、unpackaged 初始化、publish、Picker 和
   windowing 差异。
3. 确认第 3.3 节的检查项。
4. 执行自动门，并从 staging 做 WinUI 冒烟启动。

在这一步稳定之前，不开始组件化或布局扁平化。

### WP3 — Windows App SDK 组件化

按第 5 节发现的最小受支持组件集合替换元包。重新记录体积和文件差异。每次缩减
依赖图后都从干净 staging 目录启动。

### WP4 — 共享 App / Agent staging

把 publish 和本地输出改成首选扁平布局，更新第 6.2 节列出的全部 nested 路径
契约，并加入带 fail-fast 冲突的哈希合并。从合并目录验证 App 和 Agent。

若扁平化无法按第 6.5 节完成，保留 nested 布局并继续。

### WP5 — Release staging 排除 PDB

正式 staging 和 ZIP 不含 PDB。构建产物仍有符号。App 和 Agent 仍能从 staging
启动。

本工作包不依赖 WP4 成功。

### WP6 — 版本与文档

在 WP2 稳定之后，把 `Directory.Build.props` 从 V0.43 / 0.4.3 推进到
**V0.44 / 0.4.4**，并使测试和文档与已实现的平台、布局和支持文本一致。避免 2.4
升级失败后留下名为 V0.44、实际仍是 1.8 的树。

本工作包中的文档：

- `Directory.Build.props`
- `README.md` 与 `README.zh-CN.md`
- `docs/Product.md` 与 `docs/Product.zh-CN.md`
- `docs/Development.md` 与 `docs/Development.zh-CN.md`
- `docs/Quality.md` 与 `docs/Quality.zh-CN.md`
- `docs/CHANGELOG.md` 与 `docs/CHANGELOG.zh-CN.md`
- 本计划
- 其他点名 portable staging、Windows 支持范围或平台版本的活动文档

英文仍是权威文本。中文副本在同一工作项中更新。Product 应写明 V0.4 线后半段包含
平台与发行整理，而不只是视觉打磨。

### WP7 — 最终验证与尺寸报告

生成最终 V0.44 Release staging 和对比：

```text
V0.43 baseline
Windows SDK / TFM result
WinAppSDK 2.4
WinAppSDK componentization
shared App/Agent staging or retained nested layout
PDB exclusion
V0.44 final
```

脚本只记录事实，不决定体积是否可接受。

授权后的推荐提交拆分：

```text
docs: define WinPool V0.44 platform and slimming plan
build: probe Windows SDK 28000 and unify Windows TFMs
build: upgrade Windows App SDK to 2.4
build: componentize Windows App SDK dependencies
build: merge App and Agent portable staging
build: exclude PDB from distribution staging
chore: bump product version to V0.44
docs: update Windows support and deployment docs
```

若放弃扁平化，省略或替换 merge 提交，并注明保留了 nested 布局。某一步失败时，
回滚该步。不得整棵恢复 V0.43，也不得用 trimming、删功能或改图片补救。

## 10. 自动质量门槛

使用 [Quality](Quality.md) 中的项目标准：

```powershell
dotnet restore WinPool.slnx

dotnet test WinPool.slnx `
    -c Release `
    --no-restore `
    --maxcpucount:1 `
    -m:1

dotnet build WinPool.slnx `
    -c Release `
    --no-restore `
    -m:1

dotnet list WinPool.slnx package `
    --vulnerable `
    --include-transitive
```

V0.44 还要求：

- 一份已记录的 Windows SDK / TFM 基线
- Windows App SDK 2.4 Stable，且无残留 1.8 包
- 依赖图中无未使用的 AI/ML 类包
- `PublishTrimmed` 为 false
- 正式 staging 不含 PDB
- App/Agent 同名冲突 fail-fast
- staging 从空目录重建
- 公共 runtime 文件不重复，或已文档化保留 nested 布局
- XBF/PRI、Agent 可执行文件、deps 和 runtimeconfig 完整
- 架构测试与已实现布局一致

跳过、无法执行或未运行的门槛记为 `unverified` 或 `not_required`，不得记为
`passed`。完成实施不会自动开始正式验收。是否进入正式测试由开发者决定。

## 11. 原生与人工验证

当前开发用 Windows 11 机器上至少验证：

- 解压即运行 portable staging
- App cold start
- Agent 自动启动
- 托盘图标
- 主窗口打开/关闭
- 中英文切换
- Light/Dark/System 主题
- Folder Picker
- 本机盘点
- topology
- simulation
- monitoring
- SQLite 数据位置
- App/Agent 重启
- 仅 Agent 的生命周期
- 无需预装 .NET 或 Windows App Runtime

Windows 10 22H2 x64 是公开最低版本。该机器可用时跑同一套冒烟；编译成功不能代替。
若不可用，记为 `unverified`。

Windows 11 24H2 和 25H2 同样：有对应 SKU 就跑主要回归，否则 `unverified`。

除非开发者另行扩大支持范围，不得通过降低 22H2/24H2/25H2 正确性来“修复”更旧
Windows 上的失败。

## 12. 风险与回退

| 风险 | 控制 |
| --- | --- |
| 2.4 上 WinUI、Picker、windowing 或 unpackaged 启动变化 | 先完成功能等价，再组件化或扁平化 |
| 钉死的 .NET SDK 拒绝 28000 TFM | 保留可接受的 TFM；不解开 `global.json` |
| 误删 WinUI 间接需要的组件 | 只通过 NuGet 缩减；每次裁剪后从干净 staging 启动 |
| 共享目录中同名不同内容 DLL | fail-fast；回退到 nested `Agent\` |
| 体积下降其实来自变成 framework-dependent | 在没有开发运行时的机器上验证 portable staging |
| 追任意 MiB 数字 | 无硬体积门槛；不 trimming、不改图片 |

## 13. 完成与归档门槛

只有以下条件同时成立，V0.44 实施才算完成：

- WP0–WP7 已完成且无未解决停止条件，需要时使用了文档化的 TFM 与扁平化回退；
- 产品版本为 V0.44；
- .NET 10 未变，trimming 仍关闭；
- Windows App SDK 为 2.4 Stable 且已组件化；
- 未使用的 AI/ML 类元包载荷已移除；
- App 和 Agent 仍为独立进程；
- 已实现布局（nested 或扁平）在本地输出和 staging 之间一致，并已由文档描述；
- Release staging 无 PDB，原始图片未改；
- WinForms 托盘行为未改；
- 自动门已完成，或被如实标为未验证；
- 所需原生检查有如实记录的结果；
- 英文文档与中文阅读副本与已实现结果一致；
- 相对 WP0 基线的最终体积对比已记录；
- 开发者已判断该体积结果可接受；
- 未提交无关文件或生成物；
- 未经另行授权，不得声称已 push、打 tag、做 Release、上传或部署。

已完成 V0.44 的收口声明应为：

> WinPool 已迁移到新的 Windows 平台基线。它保持 Windows 10 22H2+ 为公开最低
> 支持、双进程 portable self-contained 架构和现有产品能力，同时移除了不需要的
> Windows App SDK 组件，并在合并成功时移除了 App/Agent 之间的重复运行时文件。

实施完成不会自动开始或通过正式 V0.44 验收。实施门槛之后，由开发者决定是否进入
正式测试。阶段真正结束后，本计划冻结到 `docs/Archive/V0.44/`；之后不得为了让
历史看起来更正确而改写它。

## 14. V0.44 之后

V0.44 不预先决定后续是否做原生托盘替换、framework-dependent Lite 发行、
portable/installed 双渠道、trimming、NativeAOT、single-file、图片优化或 ARM64。

这些选择要等 V0.44 的实际文件体积报告。后续瘦身使用 V0.44 树，不再使用 V0.43
手写的 380.44 MiB 数字。
