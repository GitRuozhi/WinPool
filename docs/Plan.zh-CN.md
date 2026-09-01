# WinPool V0.44 共享运行时发行计划

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

> 本文件仅为中文阅读副本；计划权威以无 `.zh-CN` 后缀的
> [Plan.md](Plan.md) 为准。

## 0. 状态、授权与基线

- **计划状态：** 跟进范围已确认，尚未开始实施
- **创建日期：** 2026-09-01
- **基线提交：** `3bfd6192561fc590e1db2b33b1257badb97cf841`
- **工作分支：** `main`
- **当前产品版本：** V0.44
- **目标产品版本：** V0.44
- **阶段性质：** 运行时对齐之后的 portable staging 并集；不新增用户功能

开发者的非正式 staging 草稿已冻结在
[`Archive/V0.44-shared-staging-draft`](Archive/V0.44-shared-staging-draft/README.md)。
本文件是唯一的活动 Plan。产品版本 **不得** 上调。

运行时对齐实验记录在
[`V0.44 App - Agent runtime alignment experiment.md`](V0.44%20App%20-%20Agent%20runtime%20alignment%20experiment.md)。
它确认了：

```text
同名且相同：207 → 288
同名但不同：5 → 0
仅 Agent：  83 → 7
唯一文件合计：约 232 MiB
```

原先五个冲突已消除。该实验本身不授权本次 staging 变更。

开发者已经确认以下控制决定：

1. App 和 Agent 继续是两个进程。
2. App 保留 `FrameworkReference` `Microsoft.WindowsDesktop.App.WindowsForms`，
   不得设置 `UseWindowsForms`。
3. App 仍是 WinUI，不得加入 WinForms UI 代码。
4. portable staging 是两份独立 self-contained 发布的 **经碰撞检查的并集**。
5. 相对路径相同且 SHA-256 相同：只存一份。
6. 相对路径相同且 SHA-256 不同：staging 失败。禁止 last-writer-wins。
7. 仅 App 与仅 Agent 的文件保留。
8. 旧的完整 `Agent\` 运行时树从 staging 和本地运行树中移除。
9. 本地 `artifacts\$(Configuration)\` 与正式 staging 使用 **同一扁平布局**。
   禁止两套启动路径。
10. 正式 staging 不含 PDB。构建产物仍可含 PDB。
11. `PublishTrimmed` 保持 false。trimming、NativeAOT、合进程、
    framework-dependent、自定义探测和手工改 DLL 不在范围。
12. 本阶段不提升产品版本。

编写本计划不等于授权实施、push、tag、GitHub Release、二进制上传、部署或真实
存储修改。只有开发者明确要求执行本计划后才开始实施。

## 1. 目标

保留两个可执行文件：

```text
WinPool.App.exe
WinPool.Agent.exe
```

保留不同的工程依赖图：

```text
App
├── 公共 .NET / WindowsDesktop runtime 基线
├── WinUI / Windows App SDK
└── App 独有文件

Agent
├── 公共 .NET / WindowsDesktop runtime 基线
├── WinForms
└── Agent 独有文件
```

发行一个 portable 目录，内容为两套 self-contained 树的安全并集。相同文件只存
一份。以后出现同名不同内容则打包失败。

## 2. 永久安全

本阶段不改变 V0.44 的存储安全模型。

- 真实存储结构变更继续拒绝。
- 盘点和监控对存储结构保持只读。
- Agent 仍是 SQLite 的唯一正常写入者。
- IPC 协议和进程所有权不变。
- 自由存储命令仍然禁止。

## 3. 运行时对齐（树中已有）

在 `WinPool.App.csproj` 中保留：

```xml
<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />
```

并加简短注释：该引用只为对齐 App 与 Agent 的 self-contained WindowsDesktop
资产选择。

App 上不得启用 `UseWindowsForms`。

增加架构测试：

- 要求存在该 FrameworkReference；
- 禁止 App 源码使用 `System.Windows.Forms`。

## 4. 本地运行树与 staging 同一布局

**`artifacts\$(Configuration)\` 和正式 staging** 的目标布局均为：

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
├── 公共 .NET 与 WindowsDesktop runtime 文件
├── App 独有 WinUI / Windows App SDK 文件
├── Agent 独有 WinForms 文件
├── Assets/
├── PRI
└── XBF
```

nested 路径 `Agent/WinPool.Agent.exe` 退役。

必须同步的契约：

- `Directory.Build.props` 与 `Directory.Build.targets` 中 Agent 的 `OutputPath` /
  `OutDir`
- `src/WinPool.App/WinPool.App.csproj` 的 `BuildAgentRuntime` 输出和
  `PublishAgentRuntimeBesideApp`（不得把 Agent 发布到 `PublishDir\Agent\`）
- `src/WinPool.App/App.xaml.cs` 的 Agent 启动路径
- `src/WinPool.App/Services/AgentStartupRegistration.cs`
- `build/Publish-Staged.ps1`
- `build/Rebuild-WinPool.ps1`
- `tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs`
- `docs/Development.md` 与 `docs/Development.zh-CN.md`
- `docs/Quality.md` 与 `docs/Quality.zh-CN.md`

`StorageDataLocationsTests` 排除 `Agent\Data` 是数据根防护，不是 exe 路径。除非
测试实际失败，否则不要改。

## 5. 独立 publish 再合并

App 和 Agent 先分别 publish 到临时目录：

```text
temp/App
temp/Agent
```

两次 publish 均为 `win-x64`、self-contained、`PublishTrimmed=false`。

不得把 Agent 直接发布进 App 输出目录。现有 `PublishAgentRuntimeBesideApp`
与此规则冲突，必须删除或跳过。

最终 staging 从空目录重建。按相对路径合并：

| 情况 | 动作 |
| --- | --- |
| 仅 App | 复制 App 文件 |
| 仅 Agent | 复制 Agent 文件 |
| 同路径且 SHA-256 相同 | 复制一份 |
| 同路径且 SHA-256 不同 | 立即失败 |

并集时跳过 `*.pdb`。不关闭编译符号生成。

publish 之后不得手工删除框架文件。

## 6. 永久碰撞门

每次 staging 都对两棵树重新计算相对路径 + SHA-256。

以后升级 .NET、WindowsDesktop、Windows SDK / TFM、Windows App SDK、WinForms
或相关框架包，必须再过同一道门。新的独有文件允许。新的同名不同内容不允许。

若未来升级无法安全对齐，只有在开发者明确决定后，才为该发行恢复隔离的
App / Agent 运行时目录。不得自动回退、自动选一份，或加自定义探测。

## 7. 验证

自动门：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

staging 检查：

- 冲突数为 0
- 公共文件只存一份
- 仅 App / 仅 Agent 文件仍在
- Release staging 无 PDB
- PRI 和 XBF 仍在
- `PublishTrimmed=false`
- 两个可执行文件都在 staging 根，产品版本为 V0.44
- 本地 `artifacts\$(Configuration)\` 使用相同根路径
- App 启动和开机注册解析到 App 旁边的 `WinPool.Agent.exe`

从 **合并后** 的 staging 目录冒烟：

App：冷启动、WinUI、导航、PRI/XBF、主题、语言、Picker、盘点、监控、连接 Agent。

Agent：直接启动、托盘图标与命令、IPC、SQLite、盘点、监控、关闭与重启。

另外：App 从根路径启动 Agent；开机注册使用该路径；portable 运行不要求预装
.NET Runtime 或 Windows App Runtime。

缺少的操作系统矩阵记为 `unverified`，不得写成 passed。

## 8. 体积报告

记录 nested 之前与扁平之后：总大小、文件数、共享数、仅 App、仅 Agent、冲突数。

对齐实验约 232 MiB 的唯一文件集只是参考，不是硬门槛。

## 9. 非目标

本阶段不合进程、不把两套依赖图做成完全相同、不删除 WinForms 或 WinUI、不给
App 加 WinForms UI、不启用 trimming 或 NativeAOT、不改为 framework-dependent、
不加 `AssemblyLoadContext` 或自定义探测路径、不手工替换框架 DLL、不改图片、
不新增产品功能、不把产品版本升过 V0.44。

## 10. 实施顺序

1. 为 App 的 WindowsDesktop 对齐引用加注释和护栏。
2. 把本地 Agent 输出平到 App 旁边；停止 nested Agent publish。
3. 把 App 和 Agent 发布到各自临时目录。
4. 实现带 fail-fast 冲突和 PDB 排除的 SHA-256 并集合并。
5. 把 Agent 启动和开机注册指到根目录可执行文件。
6. 更新 staging 脚本、Rebuild-WinPool、架构测试、Development 和 Quality。
7. 跑自动门。
8. 对合并目录做冒烟。
9. 把体积和文件数写入 CHANGELOG。
10. 结果确认后，归档对齐实验笔记。

授权后的推荐提交拆分：

```text
build: flatten App and Agent local and staged runtime trees
docs: update portable layout and collision-gate rules
```

若实现上自然分开脚本与 App 路径改动，可以再拆。

## 11. 完成

同时满足以下条件才算完成：

1. App 和 Agent 仍为独立进程，版本仍为 V0.44。
2. 二者独立 publish。
3. portable 并集没有同名哈希冲突。
4. 公共 runtime 文件只存一份。
5. 仅 App / 仅 Agent 文件仍在。
6. 两个可执行文件都从同一本地根和 staging 根运行。
7. Agent 启动和开机注册使用该根路径。
8. 未来冲突会让 staging 失败。
9. 自动门通过，或被如实标为 `unverified`。
10. 合并目录冒烟通过。

收口声明：

> 两个独立的 WinPool 进程保持各自的依赖描述、有意对齐的共享 runtime 基线，以及
> 一份经过碰撞检查的 portable 依赖并集。
