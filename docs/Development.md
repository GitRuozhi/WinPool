# WinPool 开发约定

当前源码基线：V0.60；本轮文档与下一轮代码统一面向 **V0.61**，实施顺序见[Plan](Plan.md)。本文件描述现有所有权与持续约束，不把计划中的重构写成已实现。产品语义归[Product](Product.md)，验证归[Quality](Quality.md)；整理前的详细实现与历史证据保留在[原件](Archive/20261017-v061-document-baseline/Development-before.md)。

## 模块与所有权

C#、WinUI 3、.NET 10；SDK 由 `global.json` 固定，当前为 10.0.400／latestPatch。Windows TFM 为 `net10.0-windows10.0.26100.0`，Windows App SDK 依赖由 App 项目定义。TFM 不等于最低支持系统。

| 模块 | 所有权 |
| --- | --- |
| Domain | 稳定标识、单位、纯存储规则与容量算法 |
| Application | 来源事实、用例契约、编辑意图、纯规划与投影 |
| Execution | 封闭计划／步骤、真实执行策略、结果与回放契约；当前仍有待撤的模拟过渡职责 |
| Inventory／Monitoring | 采集／监控模型与适配边界 |
| Infrastructure.Windows | 固定内嵌采集、Windows 能力与真实操作适配；当前模拟协调与仓储适配 |
| Infrastructure.Sqlite | 事务、文档、真实操作 journal、提交回执和监控持久化 |
| Ipc／Agent.Client | 类型化 App–Agent 协议、请求与事件连接 |
| App | WinUI、输入、选择、未提交草稿、预览和呈现；不写核心 SQLite |
| Agent | 唯一真实执行与数据库写入方、采集／监控会话、生命周期与恢复 |

保持现有依赖方向，优先在已有项目拆分职责，不增加通用引擎、DSL、插件平台或公共 SDK。V0.61 将结构阶段编排从 App 移入 Agent，并让模拟预览和 Agent 提交复用 Application 纯规则；相关旧职责随替换退出，见 Plan。

## 事实、草稿与边界

- `WinPoolFacts` 保存来源、时间、类型、读取状态和关联；`WinPoolSystem`／`StorageSnapshot` 是派生视图。图形布局、管理表和导航不另存可修改关系。显示用假池／假层不进入来源或真实命令。
- 内部稳定 ID 与 provider locator 分工；名称、盘符、DiskNumber、顺序不是持久身份。未知、失败、不支持、否、零和成功空集合分别表达，不用默认值制造可写或无保护角色。
- 关系只按准确唯一证据组合。分区联合体、派生对象和字段来源详见[UnifiedModel](Reference/UnifiedModel.md)，不在此复制其字段表。
- 显示合并缓存可以保留旧事实；真实临写事实必须新鲜且覆盖本次准确目标闭包。局部删除只依据完整成功覆盖域中的 absence；迟到结果不能覆盖新代事实。共享 Primordial 不扩大到无关兄弟盘。
- `SimulationEditingSession` 已有基线、ContentRevision、BindingGeneration、意图与 undo/redo。草稿与提交结果分开；外部改动保留草稿并报告冲突，传输失败不等于未提交。预览不能把本机来源重种成模拟来源。
- 校验放在外部输入、持久化读回及真实临写边界，边界内复用类型化结果。不要每层重复 `IsValid`、hash、编码再解码或日志再抛出。保留实质事务、身份、权限和 Unknown 语义。
- `InvalidDataException` 不属于 `IOException`；连接超时、无效文档和保存失败由拥有该操作的异步边界呈现一次，其余层不静默吞错。

真实 Apply 仍受 MAX 与 GPT 分段约束：新对象身份、MAX 实际容量、provider 自动 MSR 都须从已验证结果取得，不能预造未来事实。写调用前先耐久记录 `CallIssued`；已发出调用的结果未知时不重放。新计划不再使用原生 MAX，但旧持久计划及恢复记录不能被重解释成新算法。

## 当前数据与通信

| 来源 | 写入方与用途 |
| --- | --- |
| `app-settings.json` | App；语言、主题、页面偏好 |
| `agent-settings.json` | Agent；监控、采样、自启、7z 路径；App 经 IPC 修改 |
| `winpool.db` | Agent；来源文档、模拟系统、工作区、操作计划／步骤／事件／提交回执 |
| `monitoring.db` | Agent；监控会话、样本及编辑缺口 |
| `MonitoringArchives` | Agent；封存库、压缩包、恢复账本，不是历史浏览库 |

标准数据根为 `%LocalAppData%\WinPool`，便携位置为用户明确选择的程序旁 `Data`；位置指针为 `%LocalAppData%\WinPool.storage-location.json`。切换根前停止相关写入、等待后台任务，保留已提交 WAL 数据和可恢复原件；恢复路径限定目标根，不继续写旧根。允许重建开发数据不等于允许自动抹除未知目录。

现有内部格式：核心 SQLite **19**、监控 SQLite **2**、IPC **13**、StorageSystemDocument **3**、SourceFacts **1**、StorageSnapshot **3**。这些不是产品版本。已有核心 17→18→19 与监控 1→2 迁移；旧文档仍明确拒绝。V0.61 将按 Plan 迁移模拟回执表以解除旧执行计划外键和整文必填依赖；只做现有机制内的最小 schema 变化，不新建通用迁移框架，不破坏真实操作 journal 和未知结果屏障。当前尚未执行迁移。

模拟提交已有事务、基线 revision/hash CAS、commitId 幂等和丢回执查询。当前提交回执仍依赖执行计划及历史文档，V0.61 迁移时须同时改写入与 lookup，不能只删除计划保存。导入／创建／按需加载需要正文，与正常编辑的轻量提交不是同一个合同。

现有控制请求 pipe 和事件 pipe 分开；请求配对锁不能直接删除。当前帧上限 4 MiB，模拟文档存储上限 3 MiB，校验真实 UTF-8 envelope 大小，考虑 JSON 转义开销。App／Agent 严格同协议升级，不维持双生产协议。未知提交先查原 commitId／OperationId，不换 ID 重发。

监控当前仍有完整快照轮询，V0.61 才迁到有界样本批次。采样、传输和绘图频率分开；正常窗口淘汰不计为持久化丢样。真实编辑缺口保留原 operation/step、计数器族和开始时间，只有对应实际样本恢复才结束，不为停止／重启补造历史端点。轮换账本使用根内相对路径，完整性与内容校验后才释放封存原库，失败保留有效数据。

## 启动、退出与采集

App 可先以只读 SQLite 快照预显最后页面和系统；不取得写租约、不迁移数据库、不将历史呈现为新采集。Agent 连接后确认工作区并运行存储／完整硬件采集。两者仍是当前启动行为，V0.61 统一 collector 与按需正文加载的细节见 Plan。

采集固定内嵌，使用封闭 purpose/scope 参数经 stdin 执行。当前 Storage 默认 discovery、字符串补丁及重复采集的缺陷见 Plan，不能把无 discovery 探针对照当成产品已修复。布局与容量查询保留各自语义，不以 provider 上界作为已预留容量。

普通启动复用单实例；提权是整套 App＋Agent 交接。新 bootstrap 在旧进程正常退出并按 PID、启动时间、路径核实前不接管旧 endpoint。等待可取消；退出取消并等待启动恢复和采集，不能迟到发布 Ready。可选监控恢复失败报告诊断，不令整个 Agent 永久不可用；不自动无限重试。

真实写入已接受后不依赖 App pipe 存活；Agent 重启只读恢复，不自动续写。关闭主窗口不等于托盘 Agent 已退出。产品内恢复和授权规则见 Product，开发者目标授权只在 AGENTS 维护。

## 构建与便携运行树

在仓库根，先关闭已核实的标准运行树 App／Agent，再按需要运行：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

`artifacts/trees/Release/App` 与 `Agent` 为独立输出，`artifacts/Release` 为并集运行树；`artifacts/obj`、`artifacts/build` 为中间文件。App 的 `win-x64.pubxml` 和 Agent 项目已有 .NET 自包含配置，App 同时自包含 Windows App SDK。保持不裁剪，应用资源来自 `assets`，代码不依赖忽略目录或其它仓库。

`Directory.Build.targets` 调用 `build/Merge-RuntimeTrees.ps1`，相同路径按 SHA-256 去重，冲突失败。新树验证后再替换旧树，旧树移入项目根 Rubbish；现有便携 `Data` 会保留。因此 **不能直接压缩整个标准 Release 目录用于分发**。

现无生成分发 zip 的脚本。V0.61 仅补一个 `build/Package-WinPool.ps1`：复用已验证的独立树和现有 merge，使用 `-SkipPdb`、全新 `artifacts/packages/` 暂存路径，排除开发数据，再生成 zip 与 SHA-256。具体步骤见 Plan；本轮不创建脚本或包。现有 merge 限定 checkout 内路径，不要求仓库外 staging，也不为打包放宽保护。

`Rebuild-WinPool.ps1` 会移动中间输出、重建并写快捷方式，不是每次打包或检查的前置。`Clean-WinPool.ps1` 和完整性标签修复脚本只在对应问题需要时使用，不进行例行清理或重复 ACL 调查。运行树替换前的 `Assert-RealOperationIdle.ps1` 保留；持久 Unknown 与实际存活进程的区别按当前守卫处理，程序替换不解除产品写屏障。

## 文档与版本

| 内容 | 唯一维护位置 |
| --- | --- |
| 用户操作和运行入口 | 根中英文 README |
| 产品语义、能力和路线 | Product；对象与控件专项按链接查阅 |
| 技术所有权、数据、构建 | Development |
| 验证规则、命令及当前证据索引 | Quality |
| 本轮顺序、固定决定、状态 | 唯一 `docs/Plan.md` |
| 重要已完成变化 | CHANGELOG |
| 授权、文件处置、Git | AGENTS |

只更新变化事实的归属位置，不复制历史数值、逐日过程或完整对话。`docs/` 根仅放 Product、Development、Quality、Plan、CHANGELOG。按需资料分为 [Reference](Reference/README.md) 的专项说明／调查／源码入口、[Review](Review/README.md) 的本轮审查来源、[Design](Design/README.md) 的未来方案和 [Archive](Archive/README.md) 的阶段历史；只有活动 Plan 采纳的部分进入实施范围。结束或被替代的文档按本项目规则原样归档，不伪称完成；普通小修不新增 Plan、报告或副本表。

唯一版本源为 `Directory.Build.props`：`WinPoolVersionMajor=0`、`Minor=6`、`Iteration=0` 当前显示 V0.60。**用户已明确：现在只写 V0.61 文档目标；下一轮代码修改收口时将 Iteration 改为 1，生成 V0.61／0.6.1／0.6.1.0，其他字段按现有公式派生。** 不用另起版本分阶段，不创建迭代 10。V0.68／V0.69 仅在未来确需迭代时收紧范围，首稿不为用完版本号等待。
