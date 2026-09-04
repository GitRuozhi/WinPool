# WinPool 多版本发行体系修改计划（简化版）

> 适用基线：V0.45 之后  
> 原则：单一代码库、单一数据模型、单一主版本，不支持 Standard / Portable / Preview 并存。

---

## 1. 目标

WinPool 只维护一套源码。

最终提供三个发行版本：

| 版本 | 功能 | 打包方式 | 运行时 |
| --- | --- | --- | --- |
| Standard | Stable 功能 | MSIX | .NET 自包含；Windows App SDK 可依赖系统/Store framework |
| Portable | Stable 功能 | 7Z | 完全自包含 |
| Preview | Stable + Preview 功能 | MSIX | 完全自包含 |

核心关系：

```text
Standard
= Stable + MSIX

Portable
= Stable + Portable

Preview
= Stable + Preview + MSIX
```

Portable 不是独立功能版本，只是 Standard 的便携发行方式。

Preview 是测试通道，不是高级版。

---

## 2. 功能成熟度

只保留两个状态：

```text
Stable
Preview
```

功能生命周期：

```text
开发
  ↓
Preview
  ↓
用户测试 / 修复 / 完善
  ↓
Stable
  ↓
Standard + Portable
```

当前建议：

| 功能 | 状态 |
| --- | --- |
| Manage | Stable |
| Edit | Stable |
| Monitor | Stable |
| Settings / About | Stable |
| Hardware Engine | Stable |
| Hardware Page | Preview |
| Disk Test | Preview |
| Developer Page | Preview |
| AI | Preview |

以后 Hardware Page 测试完成，只需要：

```text
Hardware Page
Preview -> Stable
```

Standard 和 Portable 就自动获得该页面。

---

## 3. Hardware 规则

完整 Hardware Engine 在三个版本中都保留。

包括：

```text
HardwareReportFactory
RawHardware
KsReferenceReportFactory
HardwareFull
StorageFast
```

### Standard / Portable

正常使用：

```text
只执行 StorageFast
```

用于：

```text
Manage
Edit
Monitor
```

当前不显示 Hardware Page。

### Preview

允许：

```text
StorageFast
HardwareFull
```

并显示完整 Hardware Page。

Hardware Page 成熟后再进入 Stable。

---

## 4. Edition

增加一个简单构建属性：

```text
WinPoolEdition
```

只允许：

```text
Standard
Portable
Preview
```

不要建立三套 App 项目。

仍然使用：

```text
WinPool.App
WinPool.Agent
WinPool.Application
WinPool.Infrastructure.*
```

构建时决定当前 Edition。

---

## 5. Feature Flags

不要在代码里到处判断：

```csharp
Edition == Preview
```

集中定义：

```text
HardwarePage
DiskTest
DeveloperTools
AI
```

例如当前：

```text
                Standard   Portable   Preview

HardwarePage       No         No        Yes
DiskTest           No         No        Yes
DeveloperTools     No         No        Yes
AI                 No         No        Yes
```

页面导航只判断：

```text
Feature Enabled?
```

不直接关心当前 Edition。

---

## 6. Preview-only 重依赖

页面隐藏不代表依赖必须全部打进正式版。

对于体积较大的 Preview 功能：

```text
AI
Disk Test
外部测试工具
大型模型/runtime
```

使用条件引用。

目标：

```text
Standard / Portable
不携带不需要的 Preview 重依赖

Preview
才携带对应依赖
```

Hardware Engine 本身是公共能力，不属于 Preview-only payload。

---

## 7. 数据策略

不做 Edition 数据隔离。

不要建立：

```text
Standard.db
Preview.db
Portable.db
```

也不要建立：

```text
Data/Standard
Data/Preview
Data/Portable
```

Edition 不参与数据模型设计。

统一使用现有 WinPool 数据格式。

概念：

```text
WinPool.db
settings
cache
inventory
```

三个发行版本都使用同一套数据结构。

---

## 8. 数据兼容规则

虽然不隔离数据，但 Preview 不能破坏 Stable 数据格式。

规则：

> Preview 可以新增数据，但不要破坏 Stable 已有数据结构。

例如 Preview 可以新增：

```text
HardwareHistory
DiskTestHistory
AiSettings
```

但不能让 Standard 因为这些数据存在而无法启动。

数据库 migration 应尽量保持：

```text
向前兼容
可忽略未知数据
```

不要让 Preview 随意破坏核心表结构。

---

## 9. 不支持多版本并存

产品不设计：

```text
Standard + Preview + Portable
同时运行
```

因此当前不需要：

```text
Edition-specific database
Edition-specific IPC namespace
Edition-specific Agent
Edition-specific cache
```

也不需要为了并存增加复杂 Runtime Scope。

要求只保留：

> 同一时刻只运行当前安装/解压的 WinPool。

---

## 10. Standard

定位：

```text
正式稳定版
```

发行：

```text
MSIX
```

未来：

```text
Microsoft Store
```

特点：

```text
只显示 Stable 功能
不显示未完成页面
```

用户看到的产品名称保持：

```text
WinPool
```

---

## 11. Portable

定位：

```text
Standard 的便携发行
```

功能：

```text
与 Standard 一致
```

发行：

```text
7Z
```

要求：

```text
解压即用
.NET 自包含
Windows App SDK 自包含
Agent 自包含
不依赖系统已安装对应 runtime
```

继续复用现有：

```text
App publish
Agent publish
SHA-256 runtime union
```

规则：

```text
same path + same hash
-> 保留一份

App-only
-> 保留

Agent-only
-> 保留

same path + different hash
-> FAIL
```

Portable 文件名建议：

```text
WinPool-V0.45-x64-Portable.7z
```

---

## 12. Preview

定位：

```text
公开测试版
```

用途：

```text
让用户测试正在开发的功能
收集问题
验证 UI
验证性能
验证兼容性
```

发行：

```text
MSIX
```

运行时：

```text
完全自包含
```

即：

```text
.NET self-contained
Windows App SDK self-contained
```

Preview 可以包含：

```text
Hardware Page
Disk Test
Developer Page
AI
以及以后正在开发的新功能
```

用户名称：

```text
WinPool Preview
```

---

## 13. Preview 与 Standard 的关系

Preview 永远包含：

```text
Stable
+
当前 Preview 功能
```

例如：

```text
Standard V0.50
├── Manage
├── Edit
├── Monitor
└── Hardware

Preview V0.50
├── Manage
├── Edit
├── Monitor
├── Hardware
├── Disk Test
└── AI
```

当 Disk Test 测试完成：

```text
Disk Test
Preview -> Stable
```

下一版 Standard / Portable 自动包含。

---

## 14. 版本号

三个发行版保持同一个主版本。

例如：

```text
V0.50
```

则：

```text
Standard
V0.50

Portable
V0.50

Preview
V0.50 Preview
```

可以额外增加：

```text
PreviewRevision
```

例如：

```text
V0.50 Preview 3
```

但不要建立独立 Preview 产品版本线。

---

## 15. About 页面

显示当前发行信息。

Standard：

```text
WinPool V0.50
Standard
```

Portable：

```text
WinPool V0.50
Portable
Self-contained
```

Preview：

```text
WinPool V0.50 Preview
Self-contained
```

Bug report / diagnostics 也记录 Edition。

---

## 16. 构建入口

新增统一发布入口：

```text
build/Publish-WinPool.ps1
```

支持：

```text
-Edition Standard
-Edition Portable
-Edition Preview
```

可提供三个简单包装脚本：

```text
Build-Standard.ps1
Build-Portable.ps1
Build-Preview.ps1
```

但内部逻辑必须复用。

不要复制三套 publish 流程。

---

## 17. 发布流程

统一前半段：

```text
restore
build
test

publish App
publish Agent

SHA-256 runtime union
```

然后根据 Edition 分流。

### Standard

```text
Merged Payload
    ↓
MSIX
```

### Portable

```text
Merged Payload
    ↓
7Z
```

### Preview

```text
Merged Payload
    ↓
Self-contained MSIX
```

---

## 18. MSIX

Standard 与 Preview 都使用 MSIX。

当前阶段不需要为了支持两者同时安装而设计复杂共存机制。

只需要保证：

```text
Standard MSIX 可以安装
Preview MSIX 可以安装
```

如果以后决定必须支持二者并存，再单独增加不同 Package Identity 和 Runtime Scope。

现在不提前承担这个复杂度。

---

## 19. Portable 数据位置

Portable 可以继续使用程序目录下的数据目录，例如：

```text
WinPool/
├── WinPool.App.exe
├── WinPool.Agent.exe
├── Data/
└── ...
```

但数据 schema 与 Standard / Preview 保持一致。

Portable 不建立专属业务数据模型。

---

## 20. Disk Test

当前：

```text
Preview
```

如果需要：

```text
diskspd.exe
fio.exe
其他外部测试工具
```

则只允许 Preview 构建携带。

Standard / Portable 继续禁止这些 Preview-only 工具进入发行树。

成熟后再决定是否成为 Stable payload。

---

## 21. AI

当前：

```text
Preview
```

Standard / Portable 不携带：

```text
AI runtime
ML runtime
模型
其他大型 Preview-only dependency
```

Preview 按实际功能需要条件引用。

AI 完成后再决定是否晋级 Stable。

---

## 22. Developer Page

当前：

```text
Preview
```

Standard / Portable 不显示入口。

如果页面本身没有大型额外依赖，可以继续保留代码。

不需要专门建立 Developer Edition 或 Internal Edition。

---

## 23. 自动测试

新增简单 Edition tests。

检查：

```text
Standard
-> Stable features

Portable
-> Stable features

Preview
-> Stable + Preview features
```

检查：

```text
Standard HardwarePage == false
Portable HardwarePage == false
Preview HardwarePage == true
```

检查：

```text
Standard / Portable 不包含 Preview-only 重依赖
Preview 包含当前需要的 Preview payload
```

继续检查：

```text
runtime collision == 0
```

---

## 24. 发布验证

### Standard

验证：

```text
MSIX 可安装
App 可启动
Agent 正常
Stable 页面正确
Preview 页面不可见
```

### Portable

验证：

```text
7Z 可解压
无 .NET 环境可启动
无 Windows App Runtime 环境可启动
App / Agent 正常
Stable 页面正确
```

### Preview

验证：

```text
MSIX 可安装
完全自包含
App / Agent 正常
Preview 页面可见
Preview 功能可测试
```

---

## 25. 实施顺序

### Phase 1

增加：

```text
WinPoolEdition
Feature Flags
```

先只控制页面入口。

### Phase 2

把：

```text
Hardware Page
Disk Test
Developer
AI
```

接入 Feature Flags。

### Phase 3

把现有 Portable staging 正式定义为：

```text
Edition = Portable
```

生成 7Z。

### Phase 4

实现 Standard MSIX。

### Phase 5

实现 Preview self-contained MSIX。

### Phase 6

为 Preview-only 大型依赖增加条件引用。

### Phase 7

增加 Edition build/test matrix。

---

## 26. 非目标

当前不做：

```text
多版本同时安装支持
不同 Edition 数据库
不同 Edition 数据目录
不同 Edition IPC namespace
Internal Edition
三套源码
三条长期分支
三份 CHANGELOG
```

也不：

```text
合并 App / Agent
启用 trimming
NativeAOT
single-file
```

---

## 27. 最终模型

```text
                        WinPool
                          │
                ┌─────────┴─────────┐
                │                   │
              Stable             Preview
                │                   │
        ┌───────┴───────┐           │
        │               │           │
     Standard        Portable     Preview
       MSIX             7Z         MSIX
        │               │           │
   Stable 功能      Stable 功能   Stable + Preview
                        │           │
                self-contained   self-contained
```

最重要的原则：

> 版本区别只影响“哪些功能显示”和“怎么打包”。

不让 Edition 进入核心业务数据模型。

Portable 是 Standard 的便携发行方式。

Preview 是公开测试通道。

功能测试完成后从 Preview 晋级 Stable，而不是复制到另一个版本中。
