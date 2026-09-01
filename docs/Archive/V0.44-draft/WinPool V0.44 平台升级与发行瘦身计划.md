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
- **阶段性质：** 平台现代化与发行瘦身；不新增用户功能

开发者已经确认以下控制决定：

1. V0.44 将 Windows App SDK 从当前 1.8 系列升级到 **2.4 Stable**。
2. V0.44 将 Windows SDK 从当前 26100 系列升级到 **28000 系列**。
3. .NET 继续使用 **.NET 10**，本阶段不更换 .NET 主版本。
4. WinPool 对外公开保证的最低 Windows 版本调整为 **Windows 10 22H2 x64**。
5. 更早 Windows 版本未来可以进行非承诺性兼容测试，但不属于公开支持矩阵，也不能阻塞 V0.44 发布。
6. Windows 11 24H2、25H2 作为主要验证平台；更新的 Windows 11 版本按可用环境补充验证。
7. V0.44 继续只提供当前的 **unpackaged、portable、完全 self-contained x64** 发行模式。
8. 多发行模式属于 V1.0 计划，不在 V0.44 引入 framework-dependent 或其他 Lite 发行版。
9. `WinPool.App` 与 `WinPool.Agent` 继续保持两个独立进程，但发行产物必须消除两套 self-contained runtime 的无意义物理重复。
10. V0.44 必须将 Windows App SDK 从完整元包收缩为实际需要的组件依赖，未使用的 AI、ML、ONNX、DirectML 等组件不得继续仅因顶层元包而进入发行目录。
11. Agent 继续使用现有 WinForms 托盘实现；V0.44 不实施原生 `Shell_NotifyIcon` 替换。
12. 欢迎图片和其他现有图片资源 **完全不做体积优化**：不压缩、不降分辨率、不换格式、不重新编码。
13. 正式发行目录和发行 ZIP 不包含 PDB；构建和诊断产物继续保留 PDB。
14. V0.44 **不启用 .NET trimming**，也不把 trimming 作为实验性完成项。
15. V0.44 不设置绝对 MiB、百分比或其他发行体积硬门槛；体积变化必须记录，但最终是否满意由开发者人工判断。
16. V0.44 只处理平台升级、依赖整理和发行瘦身，不顺带新增功能、重构大型业务模块或开放真实磁盘写入能力。

编写本计划不等于授权实施、push、tag、GitHub Release、二进制上传、部署或真实存储修改。只有开发者明确要求执行本计划后才开始实施。

---

## 1. 背景与问题定义

V0.43 当前完整可分发目录约为 **380.44 MiB**。

现有分析表明，WinPool 自有业务代码、XAML、PRI、配置和资源只占发行目录的小部分。主要体积来自：

- App 自包含 .NET Runtime；
- Windows App SDK / WinUI 运行时；
- Windows SDK .NET projection；
- Agent 自包含 .NET Desktop Runtime；
- WinForms；
- App 与 Agent 分目录发布造成的重复运行时和公共程序集；
- Windows App SDK 完整元包带入但 WinPool 不使用的功能组件；
- 正式发行目录中的调试符号。

V0.44 不通过删除产品功能来解决上述问题。

本阶段的原则是：

> **保留 V0.43 产品能力和双进程架构，通过升级平台、整理依赖图和重新设计发行文件布局，减少第三方运行时及重复文件占用。**

V0.44 不以“代码越少越好”为目标，而以：

- 依赖关系准确；
- 发行目录可解释；
- 平台版本处于受支持状态；
- 同一依赖只携带必要副本；
- portable/self-contained 行为保持稳定；

作为完成标准。

---

## 2. 目标与必达结果

V0.44 完成时必须同时满足：

1. 产品版本统一升级为 **V0.44 / 0.4.4**。
2. Windows App SDK 使用 **2.4 Stable**。
3. Windows SDK 编译基线升级到 **28000 系列**。
4. .NET 继续使用 .NET 10。
5. 对外 Windows 最低支持策略更新为 **Windows 10 22H2 x64**。
6. App 和 Agent 继续是独立可执行进程。
7. portable 发行仍然无需用户预装 .NET Runtime。
8. portable 发行仍然无需用户预装对应 Windows App Runtime。
9. Windows App SDK 不再通过完整顶层依赖无条件携带 WinPool 未使用的 AI/ML 类组件。
10. App 与 Agent 的公共 self-contained runtime 文件在最终发行树中不再保存两份完全相同的副本。
11. Agent 的 WinForms 托盘功能保持不变。
12. 欢迎图和现有图片字节内容保持不变。
13. Release staging 不包含 `.pdb`。
14. Debug/build 诊断产物仍可保留 PDB。
15. `PublishTrimmed` 保持关闭。
16. WinUI XAML、PRI、XBF、启动、导航、主题、语言、Picker、Agent、IPC、盘点、监控和 SQLite 行为无功能回归。
17. V0.44 不新增真实磁盘修改能力。

体积下降是本阶段的重要结果和证据，但**不设置硬编码发行体积完成线**。

实施应记录至少以下数据：

- V0.43 基线发行总大小；
- 平台升级后的发行大小；
- Windows App SDK 组件化后的发行大小；
- App/Agent 运行时去重后的发行大小；
- PDB 从 staging 剥离后的发行大小；
- 最终 V0.44 发行总大小；
- 各阶段文件数；
- 最大若干文件及其来源；
- App/Agent 重复文件总量变化。

---

## 3. 永久安全与产品边界

V0.44 不改变 V0.43 的存储安全模型。

以下边界继续成立：

- 真实磁盘、分区、卷、Storage Pool、Storage Tier 和 Virtual Disk 修改继续默认拒绝。
- 本阶段不实现 V0.5 真实管理操作。
- 存储盘点继续保持只读。
- 监控继续保持对存储结构只读。
- Agent 继续作为 SQLite 的唯一正常写入者。
- App 与 Agent 继续通过现有受约束 IPC 通信。
- 不因为平台升级引入自由命令执行、脚本执行、插件系统或公共自动化入口。
- 不因为 Windows SDK 28000 提供了更新 API 就自动采用 Win10 22H2 不具备的新 API。
- 任何仅存在于较新 Windows 的 API 都必须具有明确版本检测、fallback 或被排除在 V0.44 外。
- 平台升级不得降低现有 IPC 身份验证、管道 ACL、进程身份校验或协议边界。
- 平台升级不得改变 SQLite 数据所有权和写入者规则。

Windows SDK 版本表示编译时可用的 API 上限，不等同于产品公开支持的最低 Windows 版本。

---

## 4. 平台基线

### 4.1 .NET

V0.44 继续使用：

```text
.NET 10
RuntimeIdentifier: win-x64
```

本阶段不进行：

- .NET 主版本升级；
- NativeAOT；
- trimming；
- single-file 转换；
- ReadyToRun 优化实验。

现有 `PublishTrimmed=False` 原则继续保留。

---

### 4.2 Windows SDK

Windows SDK 从 26100 系列升级到 28000 系列。

需要同步审计：

- Windows-targeted TFM；
- `Microsoft.Windows.SDK.BuildTools`；
- Windows SDK projection；
- WinRT/CsWinRT 相关依赖；
- native interop 编译；
- manifest；
- 测试工程中硬编码的平台版本假设。

目标是让活动 Windows 项目在一致、可解释的 SDK 基线上编译。

不得因为升级 SDK 而无条件调用只存在于新 Windows 的 API。

---

### 4.3 Windows App SDK

Windows App SDK 从当前 1.8 系列升级到：

```text
Windows App SDK 2.4 Stable
```

升级首先以**功能等价**为目标。

第一步不得同时依赖 trimming、业务架构重构或托盘重写来让应用恢复工作。

升级后需要确认：

- App 正常启动；
- XAML 初始化正常；
- PRI/XBF 正常加载；
- Windowing 正常；
- Folder/File Picker 正常；
- 主题与强调色正常；
- 中英文运行时切换正常；
- DPI / 多显示器基础行为正常；
- Agent 启动和关闭正常；
- App-Agent IPC 正常；
- unpackaged self-contained publish 正常。

---

## 5. Windows 支持策略

### 5.1 公开支持

V0.44 对外公开保证：

```text
Windows 10 22H2 x64
Windows 11 24H2 x64
Windows 11 25H2 x64
```

Windows 11 后续仍受支持的新版本可在验证后补充。

V0.44 不提供 ARM64 或 x86 发行。

---

### 5.2 更旧系统

更早 Windows 版本：

- 可以启动；
- 可以由开发者未来自行测试；
- 可以在低成本前提下保持兼容；
- 但不属于公开保证范围；
- 不要求建立完整回归环境；
- 更旧系统特有问题默认不阻塞 V0.44。

代码不得为了主动阻止旧系统运行而加入无必要版本锁。

产品文档应准确使用：

> 最低受支持版本

而不是：

> 低于此版本技术上绝对无法运行。

---

## 6. Windows App SDK 组件化瘦身

### 6.1 目标

当前完整 `Microsoft.WindowsAppSDK` 引用必须重新审计。

V0.44 不允许仅因为引用顶层元包，就把没有代码消费者的组件继续放入 portable 发行目录。

必须基于 Windows App SDK 2.4 的实际 NuGet dependency graph 确定 WinPool 所需的最小受支持组件集合。

预计核心需求包括：

- WinUI；
- WinUI 所需 Foundation/Base/runtime 组件；
- WinPool 实际使用的平台功能及其传递依赖。

具体组件包名称以 Windows App SDK 2.4 实际 NuGet 图为准，不在计划中提前硬编码不存在实际验证的依赖组合。

---

### 6.2 必须排查的非核心组件

至少审计：

- AI；
- ML；
- ONNX Runtime；
- DirectML；
- Widgets；
- DWrite；
- 其他通过完整元包进入但源码没有消费者的 Windows App SDK 子组件。

判定标准不是名称看起来“不需要”，而是：

1. NuGet dependency graph；
2. 编译引用；
3. 发布文件 provenance；
4. App 实际运行验证。

不得直接从发行目录手工删除 DLL 来模拟依赖瘦身。

正确做法必须是从**项目依赖图**移除不需要的组件，让 publish 自然生成正确文件集合。

---

### 6.3 组件化完成条件

组件化后：

- restore 成功；
- build 成功；
- tests 成功；
- publish 成功；
- App 可以从干净 staging 独立启动；
- WinUI/XAML 正常；
- Picker 正常；
- 未使用 AI/ML 等组件不再出现在 dependency graph 和最终 staging；
- 不存在依赖某个开发机全局安装组件才能运行的隐藏条件。

---

## 7. App / Agent 发布拓扑重构

### 7.1 保持双进程

运行模型继续是：

```text
WinPool.App.exe
    WinUI 外壳、导航、页面、用户交互

WinPool.Agent.exe
    托盘、盘点、监控、SQLite 单写入、IPC 和生命周期
```

不得为了减少体积：

- 将 Agent 合并进 App；
- 将监控移回 App；
- 放弃托盘生命周期；
- 改变 App/Agent 崩溃隔离；
- 改变现有 IPC 安全边界。

---

### 7.2 当前问题

V0.43 将 Agent 发布到独立的：

```text
Agent/
```

子目录。

App 和 Agent 又都采用 self-contained 发布，因此最终形成：

```text
WinPool.App runtime
+
Agent/WinPool.Agent runtime
```

其中存在大量字节完全相同的 .NET 和 Windows 相关文件。

这是 V0.44 的主要结构性瘦身目标。

---

### 7.3 V0.44 目标结构

最终发行应尽可能形成一个统一 dependency closure：

```text
WinPool/
├── WinPool.App.exe
├── WinPool.App.dll
├── WinPool.App.deps.json
├── WinPool.App.runtimeconfig.json
│
├── WinPool.Agent.exe
├── WinPool.Agent.dll
├── WinPool.Agent.deps.json
├── WinPool.Agent.runtimeconfig.json
│
├── WinPool.*.dll
├── System.*.dll
├── Microsoft.*.dll
├── .NET runtime native files
├── Windows App SDK / WinUI runtime files
├── WinForms Agent-only files
├── Assets/
└── XBF / PRI / other required resources
```

两个可执行文件继续拥有自己的：

- apphost；
- deps 文件；
- runtimeconfig；
-入口程序集；
-进程生命周期。

公共依赖只保存一份物理文件。

---

### 7.4 合并规则

不得简单执行：

```text
publish App
publish Agent
copy / overwrite
```

而不检查冲突。

必须建立确定性的 staging 合并规则：

1. App publish 输出和 Agent publish 输出分别生成到临时目录。
2. 对同名文件计算内容或可靠哈希。
3. 同名且内容完全相同：
   - staging 中只保留一份。
4. 同名但内容不同：
   - staging 构建立即失败；
   - 不允许“最后复制者覆盖”。
5. Agent 独有 WinForms 文件正常保留。
6. App 独有 WinUI / Windows App SDK 文件正常保留。
7. 最终 staging 必须从空目录重新构造，不允许依赖上一次发布残留文件。
8. staging 完成后验证 App 与 Agent 都从该同一目录运行。

这个阶段解决的是**物理重复文件**，而不是试图让两个 CLR 进程共享内存中的 Runtime 实例。

---

## 8. PDB 与发行符号

V0.44 不关闭符号生成。

构建树继续允许：

```text
*.pdb
```

以支持：

- 本地调试；
- 崩溃定位；
- stack trace 分析；
- 开发验证。

正式 portable staging 和发行 ZIP：

```text
不得包含 *.pdb
```

PDB 剥离应发生在发行 staging 规则，而不是通过全局关闭编译符号。

测试必须确认：

- build artifacts 中仍存在预期 PDB；
- distribution staging 中不存在 PDB；
- PDB 剥离不会误删 `.dll`、`.json`、`.pri`、`.xbf` 等运行文件。

---

## 9. 图片与静态资源边界

V0.44 明确不优化图片。

以下操作全部不在范围：

- PNG 压缩；
- 图片重新编码；
- 降低分辨率；
- 转 JPEG；
- 转 WebP；
- 删除欢迎图；
- 修改欢迎图随机逻辑；
- 图片打包格式实验。

现有欢迎图片及其发布行为保持功能等价。

因此 V0.44 的体积改善不得通过修改图片数字来掩盖 runtime 和 dependency graph 的实际变化。

---

## 10. Agent 与 WinForms 边界

Agent 继续：

```xml
<UseWindowsForms>true</UseWindowsForms>
```

V0.44 不进行：

- `Shell_NotifyIcon` 原生重写；
- message-only window 重构；
- WinForms context menu 替换；
- WinForms runtime 删除。

WinForms 在 V0.44 中被视为**已知、有当前消费者的依赖**。

它的发行体积可以记录，但不能作为 V0.44 未完成问题。

未来如果决定移除 WinForms，应作为独立版本计划执行，而不是隐藏在平台瘦身提交中。

---

## 11. 明确禁止的 trimming 工作

V0.44 保持：

```xml
<PublishTrimmed>False</PublishTrimmed>
```

本版本：

- 不启用 partial trimming；
- 不加入 linker descriptor；
- 不建立 trimming annotations；
- 不以 `DynamicDependency` 等方式开始裁剪兼容工程；
- 不因为发布体积仍然较大而临时开启 trimming。

原因是 V0.44 已经同时涉及：

- Windows SDK major baseline 更新；
- Windows App SDK 1.x → 2.x；
- NuGet component graph 重构；
- portable staging 重构。

trimming 将引入额外的反射、XAML、JSON、MVVM 和运行时可达性变量，不属于本阶段风险预算。

---

## 12. 实施阶段

### Phase 0 — 基线冻结与测量

实施任何升级前：

1. 记录基线 commit。
2. 从干净工作树执行标准 restore/build/test。
3. 生成 V0.43 Release portable staging。
4. 记录：
   - 总大小；
   - 文件数；
   - App 部分大小；
   - Agent 部分大小；
   - App/Agent 重复文件大小；
   - PDB 大小；
   - Windows App SDK 相关文件大小；
   - .NET runtime 相关文件大小；
   - WinForms 文件大小；
   - WinPool 自有文件大小。
5. 保存文本化报告，不提交大型临时二进制分析结果。

基线检查失败时必须记录为 `failed` 或 `unverified`，不能继续把它描述成 V0.43 已通过状态。

---

### Phase 1 — 产品版本升级

将唯一版本源从：

```text
V0.43 / 0.4.3
```

升级到：

```text
V0.44 / 0.4.4
```

更新版本相关测试和文档引用。

不得在多个工程独立维护不同产品版本。

---

### Phase 2 — Windows SDK 28000

升级 Windows SDK 编译基线。

完成：

- Windows-targeted TFM 审计；
- BuildTools 更新；
- Windows projection 依赖更新；
- 编译错误修复；
- analyzer/warning 复核。

这一阶段只解决 SDK 编译兼容问题，不主动采用新的 Windows 28000-only 产品功能。

完成后单独 restore/build/test。

---

### Phase 3 — Windows App SDK 2.4

将 Windows App SDK 升级到 2.4 Stable。

第一轮保持完整功能依赖，优先取得功能等价基线。

修复：

- breaking API changes；
- WinUI 编译变化；
- XAML tooling/resource 变化；
- unpackaged initialization；
- publish/resource staging；
- Picker/windowing 生命周期差异。

完成后执行完整自动测试和 WinUI smoke test。

只有这一阶段稳定后才进入依赖组件化。

---

### Phase 4 — Windows App SDK 组件化

替换完整 Windows App SDK 元包。

建立最小、受支持的组件依赖集合。

验证 dependency graph 后移除没有当前消费者的：

- AI；
- ML；
- ONNX；
- DirectML；
- 以及其他确认无消费者的可选组件。

重新记录发行大小和文件差异。

---

### Phase 5 — App / Agent 同目录 runtime 去重

修改发布流程：

```text
App publish
      \
       deterministic merge
      /
Agent publish
        ↓
single portable staging
```

加入：

- 同名文件哈希比较；
- 相同文件去重；
- 不同文件冲突 fail-fast；
- stale file 清理；
- staging manifest/report。

App 和 Agent 必须都从最终 staging 执行验证，而不是只测试各自原始 publish 目录。

---

### Phase 6 — Release PDB 剥离

让正式 staging 明确排除 PDB。

重新验证：

- build symbol 仍存在；
- portable staging 无 PDB；
- App/Agent 正常启动。

---

### Phase 7 — Windows 支持文档更新

将产品公开支持策略更新为：

```text
Minimum supported:
Windows 10 22H2 x64

Primary:
Windows 11 24H2 x64
Windows 11 25H2 x64
```

文档应区分：

- 编译 SDK 版本；
- 最低公开支持 OS；
- 实际可能仍可运行的旧 OS。

不得把 Windows SDK 28000 错写成“WinPool 最低要求 Windows build 28000”。

---

### Phase 8 — 最终验证与尺寸报告

执行完整 V0.44 Release staging。

生成最终对比：

```text
V0.43 baseline
Windows SDK upgrade
WinAppSDK 2.4
WinAppSDK componentization
shared App/Agent staging
PDB exclusion
V0.44 final
```

体积结果只记录事实，不由脚本决定发布通过或失败。

最终是否接受体积结果由开发者人工判断。

---

## 13. 自动质量门槛

至少执行项目现有标准：

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

并额外加入 V0.44 专属验证：

- Windows SDK 版本一致性；
- Windows App SDK 版本一致性；
- 禁止旧 1.8 package 残留；
- 禁止不需要的 AI/ML package 残留；
- `PublishTrimmed` 必须为 false；
- 正式 staging 不含 PDB；
- App/Agent 同名冲突检测；
- staging 不含旧发布残留；
- App/Agent runtime 公共文件不重复存储；
- XBF/PRI 完整；
- Agent executable 和 deps/runtimeconfig 完整；
- release staging 可以从空目录重建。

跳过、无法执行或缺少所需设备的门槛必须报告为 `unverified`，不得报告为 `passed`。

---

## 14. 手工验证矩阵

### 14.1 Windows 10 22H2

这是 V0.44 最低公开支持门槛。

至少验证：

- 解压即运行；
- App cold start；
- Agent 自动启动；
- 托盘图标；
- 主窗口打开/关闭；
- 中英文切换；
- Light/Dark/System theme；
- Folder Picker；
- 本机 inventory；
- topology；
- simulation；
- monitoring；
- SQLite 数据位置；
- App/Agent 重启；
- Agent-only 生命周期；
- portable staging 无外部 .NET/Windows App Runtime 安装前提。

Windows 10 22H2 验证不能只依赖编译成功代替。

---

### 14.2 Windows 11 24H2

执行完整主要回归。

特别关注：

- WinUI；
- windowing；
- picker；
- DPI；
- 托盘；
- monitoring；
- Agent IPC。

---

### 14.3 Windows 11 25H2

执行完整主要回归。

这是 V0.44 的另一主要 Windows 11 平台。

---

### 14.4 其他版本

更新 Windows 11 或旧于 Win10 22H2 的系统：

- 可做兼容观察；
- 记录结果；
- 不构成 V0.44 公开支持承诺。

旧 Windows 测试失败不得通过降低 22H2/24H2/25H2 正确性来修复，除非开发者另行批准扩大支持范围。

---

## 15. 发布目录验收

最终 portable staging 必须满足：

- 一个明确的发行根；
- App 和 Agent 两个独立 executable；
- 公共 runtime 只有一个物理副本；
- 不存在旧 `Agent/` 独立完整 runtime 树；
- 不包含无消费者 Windows App SDK AI/ML payload；
- 不包含 PDB；
- 保留 WinForms；
- 保留全部原始图片；
- 保留 XBF；
- 保留 PRI；
- 保留 App 和 Agent 各自所需 `.deps.json`；
- 保留 App 和 Agent 各自所需 `.runtimeconfig.json`；
- portable 机器不要求预装 .NET Runtime；
- portable 机器不要求预装目标 Windows App Runtime；
- 从全新 staging 目录运行，不依赖开发机 `bin/obj` 文件。

如果组件化或目录合并导致必须安装额外 runtime 才能工作，则该方案不符合 V0.44 当前发行模式。

---

## 16. 风险与控制

### 16.1 Windows App SDK major upgrade

**风险：**

WinUI/XAML、Picker、Windowing、资源加载或 unpackaged startup 发生行为变化。

**控制：**

先完成 2.4 功能等价升级，再做 componentization。

---

### 16.2 Windows SDK 28000

**风险：**

代码无意中使用 Win10 22H2 不存在的新 API。

**控制：**

- 不把新 SDK API 可见性等同于运行时可用性；
- 新 API 必须经过 OS contract/version 检测；
- Win10 22H2 native smoke test 为发布门槛。

---

### 16.3 componentization

**风险：**

错误判断某个组件“没有直接引用”而移除 WinUI 间接运行依赖。

**控制：**

依赖移除只能通过 NuGet graph 完成，不能手工删 publish DLL；每次依赖缩减后从干净 staging 启动验证。

---

### 16.4 App / Agent 共享目录

**风险：**

两个 publish 输出包含同名但不同版本 DLL，静默覆盖后造成单进程故障。

**控制：**

同名不同内容必须 fail-fast；禁止 last-writer-wins staging。

---

### 16.5 self-contained 完整性

**风险：**

体积下降来自意外转为依赖系统已安装 runtime，而不是实际瘦身。

**控制：**

在没有开发环境依赖的干净 Windows 环境验证 portable staging。

---

### 16.6 体积目标误导

**风险：**

为了达到任意 MiB 数字引入高风险 trimming、图片质量下降或功能删除。

**控制：**

V0.44 不设硬体积门槛。尺寸由开发者人工评估。

---

## 17. 回滚策略

各阶段应保持独立、可二分提交。

推荐提交顺序：

```text
1. docs: define V0.44 platform and slimming plan
2. chore: bump product version to V0.44
3. build: move Windows SDK baseline to 28000
4. build: upgrade Windows App SDK to 2.4
5. build: componentize Windows App SDK dependencies
6. build: merge App and Agent portable staging
7. build: exclude PDB from distribution staging
8. docs: update Windows support and deployment docs
9. test: close V0.44 platform and packaging regressions
```

如果某阶段失败，应优先回滚该阶段，而不是：

- 恢复完整 V0.43；
- 启用 trimming 补救；
- 删除功能补救；
- 修改图片补救；
- 合并 App/Agent 进程补救。

---

## 18. 明确非目标

V0.44 不包含：

- 新增用户功能；
- 真实存储修改；
- V0.5 管理操作；
- WorkspaceViewModel 大型拆分；
- 通用业务架构重构；
- Agent WinForms 托盘原生化；
- 图片压缩；
- 图片格式转换；
- 删除欢迎图；
- trimming；
- NativeAOT；
- single-file；
- framework-dependent 发行；
- Lite 发行；
- MSIX 新发行模式；
- ARM64；
- x86；
- 自动更新；
- 安装器设计；
- V1.0 多发行渠道设计。

如果实施过程中发现上述事项能够额外减小体积，也只能记录为未来候选，不得自动扩大 V0.44 范围。

---

## 19. 文档更新范围

V0.44 完成前至少同步：

- `Directory.Build.props` 产品版本；
- `README.md`；
- `README.zh-CN.md`；
- `docs/Product.md`；
- `docs/Product.zh-CN.md`；
- `docs/Development.md`；
- `docs/Development.zh-CN.md`；
- `docs/Quality.md`；
- `docs/Quality.zh-CN.md`；
- `docs/CHANGELOG.md`；
- `docs/CHANGELOG.zh-CN.md`；
- 当前 V0.44 Plan；
- 与 portable staging、Windows 支持范围和平台版本有关的其他活动文档。

英文文件继续是权威文本；中文副本只供阅读。

---

## 20. 完成定义

只有以下条件同时成立，V0.44 才可以标记完成：

1. 产品版本为 V0.44。
2. .NET 10 保持稳定。
3. Windows SDK 已迁移到 28000 系列。
4. Windows App SDK 已迁移到 2.4 Stable。
5. Windows App SDK 已完成实际组件化。
6. 未使用 AI/ML 等依赖不再由完整元包进入发行目录。
7. App 和 Agent 仍为独立进程。
8. App 和 Agent 从同一个 portable dependency closure 工作。
9. 公共 self-contained runtime 不再存在两份完全相同物理副本。
10. Agent WinForms 托盘行为未改变。
11. 原始图片未修改。
12. 正式 staging 不包含 PDB。
13. 构建诊断符号仍可用。
14. trimming 仍关闭。
15. Windows 10 22H2 x64 完成最低版本验证。
16. Windows 11 24H2 和 25H2 完成主要验证。
17. restore/test/build/package vulnerability gates 完成或被准确标记为未验证。
18. App、Agent、IPC、盘点、模拟、监控、SQLite、设置和 WinUI 基础行为无已知 V0.44 回归。
19. 文档和实际发行结构一致。
20. 最终发行尺寸已经记录并与 V0.43 基线对比，由开发者人工确认结果可接受。

V0.44 的核心完成声明应能够简化为：

> **WinPool 已迁移到新的 Windows 平台基线，在保持 Win10 22H2+ 支持、双进程 portable self-contained 架构和现有产品功能的同时，移除了不需要的 Windows App SDK 组件以及 App/Agent 之间的重复运行时文件。**

---

## 21. V0.44 之后

V0.44 不预先决定以下方案是否进入后续版本：

- 原生 Win32 托盘替换 WinForms；
- framework-dependent 轻量发行；
- portable 与 installed 双渠道；
- trimming；
- NativeAOT；
- single-file；
- 图片优化；
- ARM64。

这些方向必须基于 V0.44 最终实际发行组成重新测量后再决定。

尤其是 V0.44 完成 App/Agent runtime 去重和 Windows App SDK componentization 后，应重新生成完整文件体积报告。

后续瘦身决策应基于 **V0.44 新基线**，不再使用 V0.43 的 380.44 MiB 组成推断收益。