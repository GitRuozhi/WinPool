# WinPool 硬件读取系统分析报告与重构计划

> 适用基线：当前 V0.45 架构  
> 文档性质：分析 + 重构计划  
> 目标：新增 Hardware 页面完整展示现有硬件引擎结果，同时降低读取延迟、拆清职责、保持现有只读安全边界

---

## 1. 结论摘要

WinPool 当前并不是只有一个“存储读取器”，而是保留了一套较完整的硬件采集与报告系统。

现有链路能够生成两类重要结果：

1. `StorageSnapshot`
   - 面向 Manage / Edit 等存储工作区；
   - 包含 Storage Pool、Tier、Physical Disk、Virtual Disk、OS Disk、Partition、Volume 等结构。

2. `HardwareInventoryReport`
   - 面向完整硬件报告；
   - 来自早期 KS/StatSys 迁移体系；
   - 保留 13 个分类、154 个项目定义；
   - 包含 Computer、System、Mainboard、CPU、Memory、Virtual Memory、Volume、Disk、Storage、GPU、Monitor、Network、Battery 等数据；
   - 每个项目还可以记录 Source、Status、Warning 等采集证据。

问题不在于“没有硬件引擎”，而在于：

> 当前“存储读取”和“完整硬件读取”仍然共用同一个粗粒度采集动作。

`WindowsStorageInventoryProvider.ScanAsync()` 当前仍然转调 `WindowsHardwareInventoryProvider.CollectLocalAsync()`。后者启动完整 PowerShell 采集、解析整个 `RawSnapshot`、构造 `StorageSnapshot`，随后继续构造完整 `HardwareInventoryReport`，最后才从结果中取 `Snapshot`。

因此，逻辑上叫“Storage scan”的动作，实际仍然承担了完整硬件采集成本。

本次重构建议不拆散最终数据模型，而是拆开“读取动作”：

```text
StorageFast
    -> StorageSnapshot
    -> Manage / Edit / 启动后台刷新

HardwareFull
    -> HardwareInventoryReport
    -> Hardware 页面
```

两条动作互不强制等待，结果仍可汇总进同一个 `StorageSystemDocument`。

---

## 2. 当前实现概览

### 2.1 关键代码位置

当前硬件读取系统主要分布在：

```text
src/WinPool.Infrastructure.Windows/
├── InventoryPipeline.cs
├── EmbeddedStorageInventoryScript.cs
├── HardwareReportFactory.cs
├── KsReferenceReportFactory.cs
├── RawHardware.cs
├── RawSnapshot.cs
└── EmbeddedPowerShellInventoryProvider.cs
```

相关上层代码包括：

```text
src/WinPool.App/ViewModels/WorkspaceViewModel.cs
src/WinPool.Ipc/AgentControlMessageTypes.cs
src/WinPool.Agent/
src/WinPool.Agent.Client/
src/WinPool.Application/
```

---

## 3. 当前采集链

当前主要读取链可以概括为：

```text
App / Agent
    ↓
IHardwareInventoryProvider
    ↓
WindowsHardwareInventoryProvider
    ↓
WindowsPowerShellRunner
    ↓
EmbeddedStorageInventoryScript
    ↓
Windows PowerShell 5.1
    ↓
完整 JSON
    ↓
RawSnapshot
    ├── Storage raw data
    └── RawHardware
    ↓
RawSnapshotProjector
    ↓
StorageSnapshot
    ↓
HardwareReportFactory
    ↓
HardwareInventoryReport
    ↓
StorageSystemDocument
```

其中 `WindowsPowerShellRunner`：

- 使用系统 Windows PowerShell 5.1；
- `-NoLogo -NoProfile -NonInteractive`；
- 脚本通过 stdin 输入；
- 不在发行目录落地可修改 `.ps1`；
- 有 60 秒超时；
- 执行前经过 `ReadOnlyStorageCommandPolicy`；
- 当前保持只读安全边界。

这部分设计应继续保留。

---

## 4. 现有完整硬件引擎

现有 `HardwareReportFactory` 并不是简单的磁盘 DTO 映射。

它会把 `RawSnapshot` 和 `RawHardware` 投影到早期 KS/StatSys 体系的结构化硬件项目。

当前大类包括：

```text
01 Computer
02 System / OS
03 Mainboard / BIOS
04 CPU
05 Memory
06 Virtual Memory
07 Volumes
08 Physical Disks
09 Storage / Storage Spaces
10 GPU
11 Monitor
12 Network
13 Battery
```

现有报告支持：

```text
HardwareInventoryReport
└── Items[]
    ├── Id
    ├── Category
    ├── StandardName
    ├── ChineseName
    ├── FinalValue
    ├── Sources[]
    │   ├── Source
    │   ├── Status
    │   ├── Value
    │   ├── Diagnostic
    │   └── Duration
    └── Warnings[]
```

`KsReferenceReportFactory` 仍然保存完整结构化目录定义，因此新增 Hardware 页面不需要重新设计一套硬件字段体系。

原则：

> Hardware 页面应直接消费 `HardwareInventoryReport`，不要从当前存储导向的 `InventorySnapshot / StorageObjectView` 反向拼装硬件数据。

---

## 5. 当前主要架构问题

### 5.1 Storage scan 实际仍然是 Full scan

当前：

```text
WindowsStorageInventoryProvider.ScanAsync()
    ↓
WindowsHardwareInventoryProvider.CollectLocalAsync()
    ↓
完整脚本
    ↓
StorageSnapshot + HardwareInventoryReport
    ↓
只返回 Snapshot
```

因此即使调用方只需要磁盘、分区或 Storage Spaces，仍然会读取 CPU、内存、GPU、显示器、网络、电池、BIOS 等信息。

这是当前最明确的性能浪费。

---

### 5.2 采集用途和采集成本没有区分

不同页面对实时性的要求不同。

Manage：

```text
用户需要：
- 磁盘变化尽快出现
- Pool / Partition / Volume 刷新快
- 启动时不要被 GPU / Monitor 等慢项阻塞
```

Hardware：

```text
用户需要：
- 信息完整
- 154 项尽量保留
- Source / Status / Warning 可见
- 可以接受较慢的完整刷新
```

当前两种需求被绑定在一个动作上。

---

### 5.3 `IHardwareInventoryProvider` 语义过大

目前它返回完整 `StorageSystemDocument`。

这意味着一个“硬件 provider”同时负责：

- 启动 PowerShell；
- 原始数据采集；
- 存储拓扑投影；
- 完整硬件报告投影；
- 创建本机 `StorageSystemDocument`。

职责太多。

---

### 5.4 页面与采集行为耦合

`WorkspaceViewModel` 当前直接持有 `IHardwareInventoryProvider`。

这会造成：

```text
Workspace refresh
≈ Hardware full refresh
```

新增 Hardware 页面后，如果继续复用这条链，容易出现：

```text
Manage 点刷新
→ Hardware 也被完整重读

Hardware 点刷新
→ Manage 的整个 document 也被重新构造
```

这不符合两个页面的实际需求。

---

### 5.5 当前 IPC 已经有 Inventory 概念，但语义仍偏混合

当前 IPC 已存在：

```text
CaptureInventory
CaptureManageInventory
LoadManageInventory
```

但还没有明确的 Hardware report 获取/刷新边界。

新增 Hardware 页面时不建议继续扩大 `CaptureInventory` 的模糊职责。

---

## 6. 重构目标

本次重构目标：

### 必须实现

1. 新增 Hardware 页面。
2. 完整展示现有 `HardwareInventoryReport`。
3. 保留 13 类 / 154 项结构。
4. Storage refresh 与 Hardware refresh 分开。
5. Manage 不再因为刷新存储而强制构建完整硬件报告。
6. Hardware 页面不阻塞应用启动。
7. 正常运行时仍由 Agent 负责采集和缓存。
8. 保持现有 PowerShell 只读安全策略。
9. 保持 `StorageSystemDocument` 作为完整系统文档。
10. 为后续 Native collector 迁移预留清晰边界。

### 非目标

本阶段不：

- 一次性把 154 项全部改写成 Win32 / WMI / SetupAPI / DXGI 原生实现；
- 删除 PowerShell fallback；
- 删除 `HardwareReportFactory`；
- 删除 `KsReferenceReportFactory`；
- 改变现有存储对象模型；
- 修改真实磁盘写入安全策略；
- 引入自由文本 PowerShell 执行；
- 为每一个硬件字段单独启动一个 PowerShell 进程。

---

## 7. 推荐目标架构

### 7.1 两个独立读取动作

第一阶段只拆成两个 profile：

```text
StorageFast
HardwareFull
```

不要一开始拆十几个动作。

目标：

```text
                    InventoryCoordinator
                           │
              ┌────────────┴────────────┐
              │                         │
        Storage Fast Path        Hardware Full Path
              │                         │
       StorageSnapshot          HardwareInventoryReport
              │                         │
        Manage / Edit              Hardware Page
```

---

## 8. StorageFast

职责：

```text
读取：
- Computer minimal identity
- Storage Subsystem
- Storage Pool
- Storage Tier
- Physical Disk
- Virtual Disk
- OS Disk
- Partition
- Volume
- Network Disk（如仍属于当前 Manage 需求）
- 生成 StorageSnapshot 所必需的关系字段
```

不读取：

```text
- CPU cache
- DIMM 详细数据
- GPU
- Monitor
- Battery
- 不属于 StorageSnapshot 的系统详细字段
```

目标：

> StorageFast 只读取 `RawSnapshotProjector.Project()` 真正需要的字段。

输出：

```csharp
StorageSnapshot
```

主要消费者：

```text
Manage
Edit
启动后台刷新
存储相关 inventory
```

---

## 9. HardwareFull

职责：

```text
读取完整硬件报告所需数据：
- Computer
- System / OS
- Mainboard / BIOS
- CPU
- Memory
- Virtual Memory
- Volumes
- Physical Disks
- Storage
- GPU
- Monitor
- Network
- Battery
```

输出：

```csharp
HardwareInventoryReport
```

主要消费者：

```text
Hardware Page
完整系统导出
完整 simulation/reference document
诊断/比对功能
```

触发时机：

```text
- Hardware 页面首次打开时
- 用户主动点击刷新
- 明确要求生成完整系统报告时
```

默认不阻塞：

```text
- App 冷启动
- Manage 首屏
- Manage 普通存储刷新
```

---

## 10. 数据模型不要跟着拆烂

仍保留：

```text
StorageSystemDocument
├── Snapshot
├── HardwareReport
└── Jobs
```

采集动作拆开，但聚合文档不必拆。

建议增加明确的更新函数或 coordinator：

```text
ApplyStorageSnapshot(document, snapshot)
    -> 更新 Snapshot
    -> 保留 HardwareReport

ApplyHardwareReport(document, report)
    -> 更新 HardwareReport
    -> 保留 Snapshot
```

这样：

```text
StorageFast refresh
```

不会无意义清掉硬件报告。

同时：

```text
HardwareFull refresh
```

也不会重建和覆盖刚刚更新过的存储拓扑。

---

## 11. Application contract 调整

建议从目前过大的 `IHardwareInventoryProvider` 中拆出明确接口。

目标示例：

```csharp
public interface IStorageInventoryProvider
{
    Task<StorageSnapshot> CaptureAsync(
        CancellationToken cancellationToken);
}

public interface IHardwareReportProvider
{
    Task<HardwareInventoryReport> CaptureAsync(
        CancellationToken cancellationToken);
}
```

如果需要统一入口，可在 Application 层增加：

```csharp
public interface IInventoryCoordinator
{
    Task<StorageSnapshot> RefreshStorageAsync(
        CancellationToken cancellationToken);

    Task<HardwareInventoryReport> RefreshHardwareAsync(
        CancellationToken cancellationToken);
}
```

不要让 `IInventoryCoordinator` 直接变成另一个巨大的“一次读取所有”接口。

---

## 12. Infrastructure 重构

建议调整为更明确的文件结构：

```text
WinPool.Infrastructure.Windows/
│
├── Inventory/
│   ├── WindowsPowerShellRunner.cs
│   ├── ReadOnlyStorageCommandPolicy.cs
│   └── InventoryCoordinator.cs
│
├── Storage/
│   ├── WindowsStorageInventoryProvider.cs
│   ├── EmbeddedStorageFastScript.cs
│   ├── RawStorageSnapshot.cs
│   └── RawSnapshotProjector.cs
│
└── Hardware/
    ├── WindowsHardwareReportProvider.cs
    ├── EmbeddedHardwareInventoryScript.cs
    ├── RawHardware.cs
    ├── HardwareReportFactory.cs
    └── KsReferenceReportFactory.cs
```

第一阶段不要求立刻物理移动全部文件。

优先先拆行为和接口，再整理目录。

---

## 13. PowerShell collector 重构

### 13.1 不要继续使用一个巨大脚本服务两个动作

当前 `EmbeddedStorageInventoryScript` 实际已经包含大量完整硬件采集内容。

建议拆成：

```text
EmbeddedStorageFastScript
EmbeddedHardwareInventoryScript
```

### 13.2 允许 HardwareFull 重复读取少量 Storage 字段

不要为了“绝对零重复”制造复杂依赖。

例如 Hardware report 中磁盘部分需要：

```text
Disk
Volume
Storage Spaces
```

HardwareFull 可以自己读取这些字段。

优先保证：

```text
两个动作独立
逻辑可验证
缓存明确
```

而不是为了节省几十毫秒把两个采集器重新耦合。

---

## 14. 启动流程调整

### 当前理想流程

```text
App 启动
    ↓
Agent 连接
    ↓
从 SQLite / cache 恢复上一次 StorageSystemDocument
    ↓
立即显示 Manage
    ↓
后台 StorageFast refresh
    ↓
只更新 Snapshot
```

启动过程中：

```text
不等待 HardwareFull
```

---

## 15. Hardware 页面加载流程

推荐：

```text
用户进入 Hardware
        ↓
当前 document 有 HardwareReport 缓存？
        │
   ┌────┴────┐
   │         │
   有        没有
   │         │
立即展示    显示 loading / skeleton
   │         │
   └────┬────┘
        ↓
判断是否需要后台刷新
        ↓
HardwareFull
        ↓
保存 cache
        ↓
更新 Hardware 页
```

Hardware 页顶部建议显示：

```text
最后读取时间
采集状态
耗时
刷新按钮
数据来源
```

---

## 16. Hardware 页面 UI 计划

新增：

```text
src/WinPool.App/HardwarePage.xaml
src/WinPool.App/HardwarePage.xaml.cs
```

如果继续 MVVM，可增加：

```text
src/WinPool.App/ViewModels/HardwareViewModel.cs
```

推荐布局：

```text
┌──────────────────────────────────────────────────┐
│ Hardware                     上次刷新 12:34:56  刷新 │
├──────────────┬───────────────────────────────────┤
│ Computer     │ Name                  Value        │
│ System       │ -------------------------------- │
│ Mainboard    │ CPU                   ...          │
│ CPU          │ Core count            ...          │
│ Memory       │ ...                                │
│ Virtual Mem  │                                    │
│ Volumes      │                                    │
│ Disks        │                                    │
│ Storage      │                                    │
│ GPU          │                                    │
│ Monitor      │                                    │
│ Network      │                                    │
│ Battery      │                                    │
└──────────────┴───────────────────────────────────┘
```

每个 item 至少显示：

```text
名称
值
状态
```

详情/展开区域可显示：

```text
ID
StandardName
ChineseName
Source
SourceStatus
Diagnostic
Warnings
Duration
```

原则：

- `Unavailable` 不隐藏；
- `NoData` 不伪造成空字符串正常项；
- Warning 明确显示；
- 多值字段使用列表，不强行拼成不可读的一行；
- 敏感值继续服从现有隐私开关和 sanitizer 规则。

---

## 17. Hardware 页面与当前 System 选择

Hardware 页应展示：

```text
当前 SelectedSystem.HardwareReport
```

因此支持：

```text
本机
Simulation
Imported document
KS reference document
```

对本机：

```text
允许刷新
```

对 simulation / import：

```text
只展示文档中的 HardwareReport
默认不执行本机采集
```

这样 Hardware 页面天然支持历史报告和模拟系统，而不是只做“本机硬件信息窗口”。

---

## 18. Agent 责任

正常运行时建议继续保持：

```text
App
    ↓ IPC
Agent
    ↓
Collector
    ↓
Cache / SQLite
```

不要让 HardwarePage 直接创建：

```text
new WindowsHardwareReportProvider()
```

正常 production path 中采集动作应继续由 Agent 所有。

无 Agent developer fallback 可以继续保留，但应明确是 fallback。

---

## 19. IPC 重构

建议逐步增加明确消息：

```text
GetHardwareReport
RefreshHardwareReport

GetStorageInventory
RefreshStorageInventory
```

可以先保留旧协议兼容一段时间：

```text
CaptureInventory
CaptureManageInventory
LoadManageInventory
```

但新 Hardware 页不要继续复用一个含义不明确的 `CaptureInventory`。

推荐响应：

```text
GetHardwareReportResponse
├── Report
├── CapturedAt
├── IsCached
└── Duration

RefreshHardwareReportResponse
├── Report
├── CapturedAt
└── Duration
```

Storage 响应只传 Storage 所需数据。

---

## 20. 缓存策略

建议 Storage 与 Hardware 分开记录 freshness。

例如：

```text
StorageCapturedAt
HardwareCapturedAt
```

不要再假设：

```text
document.UpdatedAt
```

能够代表所有数据同时更新。

建议状态：

```text
Storage cache:
- 高频变化
- 启动后自动后台刷新

Hardware cache:
- 低频变化
- 打开 Hardware 页时按需刷新
```

第一阶段不必引入复杂 TTL。

可以使用简单规则：

```text
Storage:
启动后刷新

Hardware:
有缓存先显示
用户进入 Hardware 时后台刷新一次
用户点击刷新时强制刷新
```

后续再根据实测调整 TTL。

---

## 21. 持久化建议

如果当前 SQLite 保存的是完整 `StorageSystemDocument`，第一阶段可以继续保存完整文档。

关键要求：

```text
Storage refresh
→ merge 新 Snapshot + 旧 HardwareReport
→ persist

Hardware refresh
→ merge 旧 Snapshot + 新 HardwareReport
→ persist
```

不要因为两个刷新动作独立而产生：

```text
last writer wins
```

导致另一部分数据回退。

Agent 内应对 document 更新做串行化或乐观版本检查。

---

## 22. 并发规则

允许：

```text
Manage 正在 StorageFast
同时用户进入 Hardware
```

但是否允许两个 PowerShell 进程同时跑，需要明确策略。

第一阶段建议：

```text
一个 inventory execution gate
```

也就是：

```text
StorageFast 与 HardwareFull 不同时启动两个 PowerShell
```

原因：

- 避免重复 WMI/CIM/Storage 查询压力；
- 避免多进程同时读取导致性能反而下降；
- 行为更容易复现和测量。

但 UI 不阻塞：

```text
StorageFast 等待 HardwareFull
或 HardwareFull 等待 StorageFast
```

只影响后台任务队列，不冻结页面。

后续如果实测证明并行更快，再调整。

---

## 23. 性能测量

在修改 collector 前先加计时。

至少记录：

```text
PowerShell process startup
Storage query
System query
BIOS
CPU
Memory
Volume
Disk
Storage Spaces
GPU
Monitor
Network
Battery
Serialization
JSON parse
Storage projection
Hardware report projection
Total
```

如果当前脚本不方便单项记录，第一阶段至少记录：

```text
StorageFast total
HardwareFull total
PowerShell process total
JSON parse
Projection
```

验收不能只看“感觉快”。

---

## 24. 后续 Native collector 方向

架构拆开以后，再逐步减少 PowerShell 依赖。

已有 `InventoryProviderKind.NativeWindows`，因此长期可以形成：

```text
                 Inventory contract
                       │
        ┌──────────────┴──────────────┐
        │                             │
 Native Windows collectors       PowerShell fallback
        │                             │
      主路径                         兼容路径
```

建议迁移顺序：

```text
Phase N1:
StorageFast native

Phase N2:
Computer / OS
CPU
Memory

Phase N3:
BIOS / Mainboard
Network
Battery

Phase N4:
GPU / Monitor / complex display fields
```

不要把 Native 化与本次 HardwarePage 上线绑死。

---

## 25. PowerShell 的长期角色

即使未来 Native collector 成为主路径，也建议保留当前 PowerShell collector 作为：

```text
fallback
reference implementation
comparison source
regression oracle
```

特别是当前硬件引擎来自早期 KS/StatSys 迁移，已有较完整的字段覆盖。

Native collector 上线时可以做：

```text
Native report
vs
PowerShell reference report
```

对比字段缺失和行为变化。

---

## 26. 分阶段实施计划

### Phase 0 — 建立基线

不改业务行为。

记录：

```text
App 冷启动耗时
首次 Manage 可用时间
当前 full inventory 总耗时
PowerShell 执行耗时
HardwareReportFactory 耗时
Storage projection 耗时
```

保存至少一台测试机的 154 项报告作为回归样本。

---

### Phase 1 — Hardware 页面

先不优化 collector。

完成：

```text
HardwarePage
HardwareViewModel
13 类导航
154 项展示
Source / Status / Warning 展示
刷新入口
敏感值处理
simulation/import 报告展示
```

先证明现有完整引擎的数据能够正确被 UI 消费。

---

### Phase 2 — Contract 拆分

新增：

```text
IStorageInventoryProvider
IHardwareReportProvider
```

停止让 `IStorageInventoryProvider` 包装整个 `IHardwareInventoryProvider`。

`WorkspaceViewModel` 的 Manage refresh 改为只依赖 Storage 路径。

---

### Phase 3 — Collector 拆分

把当前大脚本拆成：

```text
EmbeddedStorageFastScript
EmbeddedHardwareInventoryScript
```

目标：

```text
StorageFast 不采 CPU/GPU/Monitor/Battery 等无关数据。
HardwareFull 保持现有 154 项覆盖。
```

---

### Phase 4 — Agent / IPC

增加明确的：

```text
RefreshStorageInventory
GetHardwareReport
RefreshHardwareReport
```

Agent 成为两个动作的唯一正常运行协调者。

---

### Phase 5 — Cache / persistence

分别维护：

```text
Storage freshness
Hardware freshness
```

支持独立 merge 后写回完整 `StorageSystemDocument`。

---

### Phase 6 — 性能优化

根据测量结果决定：

```text
哪些查询改 Native
哪些继续 PowerShell
哪些可以并行
哪些需要缓存
```

禁止没有测量依据的大规模重写。

---

## 27. 测试计划

### StorageFast

验证：

- Physical Disk 完整；
- Pool 完整；
- Tier 完整；
- Virtual Disk 完整；
- OS Disk 完整；
- Partition 完整；
- Volume 完整；
- relationships 不丢；
- StableId 不变化；
- Manage 页面行为不回归；
- Edit 页面选择与拓扑不回归。

---

### HardwareFull

验证 13 个分类全部存在。

验证 154 个定义：

```text
Success
NoData
Unavailable
```

都可以正确表示。

特别验证：

```text
CPU
Memory
BIOS
Disk
GPU
Monitor
Network
Battery
```

多实例设备映射。

---

### 缓存

验证：

```text
启动只读取缓存也可进入 Manage
Storage refresh 不清空 HardwareReport
Hardware refresh 不回退 StorageSnapshot
```

---

### IPC

验证：

```text
App 不直接执行 PowerShell
Agent 不在线时 fallback 行为明确
请求取消有效
超时有效
重复刷新不会产生竞争写
```

---

### 安全

继续验证：

```text
固定脚本
stdin
NoProfile
NonInteractive
无外置 .ps1
ReadOnlyStorageCommandPolicy
无用户输入脚本
无 mutating storage cmdlet
```

---

## 28. Hardware 页面验收标准

Hardware 页面完成标准：

1. 13 个分类全部可访问。
2. 154 项定义没有因为 UI 筛选被静默删除。
3. `Unavailable` 明确显示。
4. `NoData` 明确显示。
5. Warning 可查看。
6. Source 可查看。
7. 多值项目可读。
8. 本机可刷新。
9. simulation/import 可显示已有报告。
10. 敏感值遵守现有隐私设置。
11. 页面打开不要求 Manage 重新扫描。
12. Hardware 刷新不重建整个 Workspace。

---

## 29. 性能验收原则

不建议现在写死一个毫秒阈值。

首先比较重构前后：

```text
Cold startup -> Manage 可用
Manage manual refresh
StorageFast
HardwareFull
```

预期方向：

```text
Manage refresh 明显快于当前 full inventory。
HardwareFull 与当前完整采集耗时大致相当或更好。
应用启动不再等待 HardwareFull。
```

如果 StorageFast 仍然慢，再进入 Native storage collector 阶段。

---

## 30. 推荐最终结构

```text
                         WinPool Agent
                              │
                    InventoryCoordinator
                              │
            ┌─────────────────┴─────────────────┐
            │                                   │
       StorageFast                         HardwareFull
            │                                   │
 IStorageInventoryProvider           IHardwareReportProvider
            │                                   │
    Storage Fast Script             Hardware Full Script
            │                                   │
     StorageSnapshot              HardwareInventoryReport
            │                                   │
    ┌───────┴────────┐                         Hardware
    │                │                           Page
  Manage            Edit
            │                                   │
            └─────────────────┬─────────────────┘
                              │
                    StorageSystemDocument
                              │
                         SQLite / cache
```

长期：

```text
PowerShell collector
       ↓
fallback / reference

Native Windows collector
       ↓
逐步成为高频读取主路径
```

---

## 31. 推荐实施顺序

建议严格按以下顺序：

```text
1. 给当前 full collector 加耗时基线
2. 新建 Hardware 页面，完整展示现有报告
3. 新建 IHardwareReportProvider
4. 让 IStorageInventoryProvider 成为真正独立的 storage contract
5. 拆 StorageFast / HardwareFull 脚本
6. Manage refresh 切到 StorageFast
7. Hardware refresh 切到 HardwareFull
8. Agent / IPC 拆分两个动作
9. 独立缓存和 freshness
10. 做回归和性能比较
11. 再决定是否 Native 化
```

不要反过来先重写整个采集器再做页面。

这样每一个阶段都有可验证结果，也更容易定位数据缺失。

---

## 32. 最终决策

本次建议采用：

> **一个完整系统文档，两条独立读取路径。**

不是：

```text
把 StorageSystemDocument 拆成多个互不相关的数据系统
```

而是：

```text
StorageFast
HardwareFull
```

分别负责不同成本、不同刷新频率的数据。

最终仍统一进入：

```text
StorageSystemDocument
```

Hardware 页面直接展示完整 `HardwareInventoryReport`。

Manage / Edit 只依赖高频的 `StorageSnapshot`。

这样可以同时达到：

```text
硬件页面完整
存储刷新更快
启动更快
职责更清楚
缓存更合理
未来 Native 化更容易
```

并继续保留现有完整硬件引擎作为 WinPool 的重要资产。
