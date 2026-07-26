# Localization and Terminology

All user-visible strings must be stored in localization resources. Chinese and English ship together from the first implementation milestone. Layouts must tolerate at least 30% text expansion.

## Product terminology

| English | 简体中文 | Usage note |
|---|---|---|
| Overview | 总览 | Top-level page |
| Physical disk | 物理磁盘 | Do not shorten to “disk” where ambiguity exists |
| Storage pool | 存储池 | Windows Storage Spaces object |
| Storage tier | 存储层 | Media-role tier inside a pool |
| Virtual disk | 虚拟磁盘 | Distinct from volume |
| Partition | 分区 | Disk or virtual-disk partition |
| Volume | 卷 | File-system volume |
| File system | 文件系统 | NTFS/ReFS |
| Allocation unit size | 分配单元大小 | “NTFS cluster size” may appear in explanatory text |
| Interleave | 交错大小 | Keep the PowerShell property name available in details |
| Number of columns | 列数 | Layout parameter, not simply physical-disk count |
| Resiliency | 复原类型 | Mirror/parity/simple context |
| Provisioning type | 预配类型 | Fixed/thin |
| Health status | 运行状况 | User-facing health |
| Operational status | 操作状态 | May contain multiple values |
| Eligible for pool | 可用于存储池 | Eligibility result |
| Read-only scan | 只读扫描 | First milestone behavior |
| Operation plan | 操作计划 | Reviewed change proposal |
| Preflight check | 前置检查 | Safety checks before execution |
| Simulation | 模拟执行 | Default mode; no mutating process starts |
| Real execution | 真实执行 | Requires an administrator process; a standard user can request a confirmed one-time elevated restart |
| Standard user | 标准用户 | Selecting Real offers a localized administrator-restart confirmation |
| Administrator | 管理员 | Necessary but not sufficient for Real execution |
| Execute | 执行 | Runs reviewed commands |
| Verification | 结果验证 | Post-execution state comparison |
| Audit history | 审计历史 | Append-only operation records |
| Operation area | 操作区 | Upper information and command area |
| Logic area | 逻辑区 | Lower complete storage topology |
| System | 系统 | Horizontal workspace category |
| Pool | 池 | Short horizontal category label for storage pools |
| Tier | 层 | Short horizontal category label for storage tiers |
| Disk | 磁盘 | Short horizontal category label; details use “physical disk” |
| Logical volume | 逻辑卷 | Horizontal category label; distinguishes volumes from virtual disks |
| Object selector | 对象标签区 | Vertical list that changes with the horizontal category |
| Execution mode | 执行模式 | Always visible in the title bar |
| Theme | 主题 | System, Light, or Dark |
| Follow system | 跟随系统 | Default theme preference |
| Language | 语言 | Chinese or English |
| Settings | 设置 | Sole upper-right title-bar action; opens a full-workspace page |
| Administrator suffix | 管理员后缀 | Append `[Administrator]` / `[管理员]` to the title only when elevated |
| Create storage pool | 创建存储池 | Initial mutating capability |
| Attention required | 需要注意 | Warning state |
| Blocked | 已阻止 | Cannot continue safely |
| Partial failure | 部分失败 | Some steps changed state |

## Writing rules

- Keep object names, serial numbers, unique IDs, cmdlet names, and command text untranslated.
- Use sentence-style labels rather than title case in English.
- Do not translate a “virtual disk” as a volume.
- Do not describe 64K+64K as an official Microsoft recommendation; use “current tested recommendation”.
- Chinese and English warning text must express the same severity and blocking behavior.
- Every icon-only action requires a localized accessible name.
- `Simulated` must be translated as `模拟完成`; it must not use the Chinese or English success label for a real operation.
- User-provided pool names, disk models, volume labels, stable IDs, and command text remain untranslated when the language changes.
