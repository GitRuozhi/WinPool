# WinPool V0.44 平台升级与发行瘦身计划

## 0. 范围

V0.44 只做两件事：

1. 平台升级；
2. portable self-contained 发行瘦身。

不新增用户功能，不修改现有产品架构和存储安全边界。

已确认：

- .NET 保持 **.NET 10**；
- Windows SDK：**26100 → 28000 系列**；
- Windows App SDK：**1.8 → 2.4 Stable**；
- 最低公开支持：**Windows 10 22H2 x64**；
- Windows 11 24H2 / 25H2 为主要验证平台；
- 更老 Windows 可以兼容，但不公开保证；
- 当前只保留 **unpackaged + portable + self-contained x64**；
- App 和 Agent 继续是独立进程；
- Agent 继续使用 WinForms 托盘；
- 图片不压缩、不换格式、不修改；
- Release 不带 PDB，构建产物保留 PDB；
- 不启用 trimming；
- 不设置发行体积硬指标。

---

## 1. 目标

V0.44 完成后：

- 产品版本升级到 **V0.44 / 0.4.4**；
- Windows SDK 使用 28000 系列；
- Windows App SDK 使用 2.4 Stable；
- Windows App SDK 只保留 WinPool 实际需要的组件；
- AI、ML、ONNX、DirectML 等无消费者组件不再进入发行目录；
- App 与 Agent 保持双进程，但共享公共 self-contained runtime 文件；
- 不再保留两套完全重复的 .NET/runtime 依赖；
- Release staging 不包含 PDB；
- portable 版本仍无需用户安装 .NET 或 Windows App Runtime；
- WinUI、Agent、IPC、盘点、模拟、监控、SQLite 和设置行为保持不变。

---

## 2. 平台升级

### 2.1 Windows SDK

升级到 28000 系列。

同步更新：

- Windows TFM；
- `Microsoft.Windows.SDK.BuildTools`；
- 相关 Windows SDK / WinRT 编译依赖。

升级 SDK 不代表放弃 Win10。

新增 API 如果 Win10 22H2 不支持，必须做版本检测或不用。

### 2.2 Windows App SDK

升级到 2.4 Stable。

先完成单纯版本升级并恢复全部现有功能，再进行依赖组件化。

重点验证：

- App 启动；
- XAML / PRI / XBF；
- Windowing；
- Folder/File Picker；
- 主题、语言和 DPI；
- Agent；
- IPC；
- portable publish。

---

## 3. Windows App SDK 组件化

不再无条件引用完整 Windows App SDK 元包。

根据 2.4 实际 NuGet dependency graph，只保留 WinPool 当前需要的组件。

重点排查并移除无消费者依赖：

- AI；
- ML；
- ONNX Runtime；
- DirectML；
- Widgets；
- 其他未使用可选组件。

不得通过手工删除 publish DLL 实现瘦身。

依赖必须从项目/NuGet graph 上正确移除。

---

## 4. App / Agent 发行去重

保持：

```text
WinPool.App.exe
WinPool.Agent.exe
```

两个独立进程。

但最终 portable staging 改为共享一个 dependency closure：

```text
WinPool/
├── WinPool.App.exe
├── WinPool.Agent.exe
├── App / Agent 各自 deps 和 runtimeconfig
├── 公共 .NET runtime
├── 公共 WinPool assemblies
├── WinUI / Windows App SDK
├── Agent 独有 WinForms assemblies
├── Assets
├── PRI
└── XBF
```

发布流程：

1. App 和 Agent 分别 publish 到临时目录；
2. 合并到最终 staging；
3. 同名且内容相同的文件只保留一份；
4. 同名但内容不同则构建失败，不允许静默覆盖；
5. 最终 App 和 Agent 都必须直接从合并后的 staging 正常运行。

V0.44 只去除磁盘上的重复文件，不改变两个进程各自独立运行 CLR 的模型。

---

## 5. PDB

继续正常生成 PDB。

构建目录保留：

```text
*.pdb
```

正式 portable staging 和 Release ZIP 排除：

```text
*.pdb
```

不通过关闭调试符号生成来瘦身。

---

## 6. 明确不做

V0.44 不包含：

- 图片压缩或格式转换；
- WinForms 托盘替换；
- trimming；
- NativeAOT；
- single-file；
- framework-dependent 发行；
- MSIX；
- ARM64 / x86；
- 新用户功能；
- WorkspaceViewModel 等业务重构；
- 真实磁盘写入能力。

---

## 7. 实施顺序

### Phase 1 — 建立基线

记录 V0.43：

- 总发行大小；
- 文件数；
- App 大小；
- Agent 大小；
- App/Agent 重复文件大小；
- Windows App SDK 相关大小；
- PDB 大小。

### Phase 2 — V0.44 版本升级

```text
0.4.3 → 0.4.4
```

### Phase 3 — Windows SDK

```text
26100 → 28000
```

完成 restore / build / test。

### Phase 4 — Windows App SDK

```text
1.8 → 2.4 Stable
```

先恢复现有功能。

### Phase 5 — Windows App SDK 组件化

移除没有消费者的组件和传递依赖。

### Phase 6 — App / Agent runtime 去重

建立统一 portable staging。

### Phase 7 — Release PDB 排除

正式发行不带 PDB。

### Phase 8 — 文档和最终验证

更新平台支持、开发、质量和发布文档，并重新记录最终发行组成。

---

## 8. 验证

标准自动检查：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

额外检查：

- Windows SDK 已统一到目标版本；
- Windows App SDK 已统一到 2.4；
- 不残留旧 1.8 依赖；
- 不残留确认无消费者的 AI/ML 类依赖；
- `PublishTrimmed=False`；
- Release staging 不含 PDB；
- App/Agent 公共文件不重复；
- 同名不同内容文件会 fail-fast；
- portable staging 可以从空目录重新生成。

无法执行的检查必须标记 `unverified`，不能写成 `passed`。

---

## 9. 系统验证

公开支持：

```text
Windows 10 22H2 x64
Windows 11 24H2 x64
Windows 11 25H2 x64
```

至少验证：

- App 启动；
- Agent 和托盘；
- IPC；
- 本机盘点；
- topology；
- simulation；
- monitoring；
- SQLite；
- Folder Picker；
- 主题和语言；
- App/Agent 重启；
- portable 环境无需预装 .NET / Windows App Runtime。

更老 Windows 可额外测试，但不属于 V0.44 发布保证。

---

## 10. 完成条件

V0.44 完成需要：

1. Windows SDK 已升级到 28000 系列；
2. Windows App SDK 已升级到 2.4 Stable；
3. Windows App SDK 已完成依赖组件化；
4. App / Agent 继续保持独立进程；
5. 公共 runtime 不再保存两份重复文件；
6. Release 不含 PDB；
7. 图片未修改；
8. WinForms 托盘未修改；
9. trimming 保持关闭；
10. Win10 22H2、Win11 24H2 / 25H2 验证完成；
11. 现有核心功能无已知回归；
12. 最终发行大小和组成已重新记录。

V0.44 的核心结果：

> **升级 Windows 平台基线，并在保持现有功能、双进程和完全 portable self-contained 发行方式的前提下，移除不需要的 Windows App SDK 组件和 App/Agent 重复运行时文件。**