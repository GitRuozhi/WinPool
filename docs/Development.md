# WinPool 开发约定

**本轮技术目标为 V0.61。** 本文件描述已落地的技术所有权与持续约束；执行、验证和候选包状态见[Plan](Plan.md)及[Quality](Quality.md)，实际版本字段以 `Directory.Build.props` 为准。产品语义归[Product](Product.md)；整理前的详细实现与历史证据保留在[原件](Archive/20261017-v061-document-baseline/Development-before.md)。

## 模块与所有权

C#、WinUI 3、.NET 10；SDK 由 `global.json` 固定，当前为 10.0.400／latestPatch。Windows TFM 为 `net10.0-windows10.0.26100.0`，Windows App SDK 依赖由 App 项目定义。TFM 不等于最低支持系统。

| 模块 | 所有权 |
| --- | --- |
| Domain | 稳定标识、单位、纯存储规则与容量算法 |
| Application | 来源事实、用例契约、编辑意图、共享模拟规则、纯规划与投影 |
| Execution | 封闭计划／步骤、真实执行策略、结果与耐久事件契约 |
| Monitoring | 采样会话、窗口、批次上游序列与持久化边界 |
| Infrastructure.Windows | 固定内嵌采集、Windows 能力与真实操作适配；轻量模拟提交及仓储适配 |
| Infrastructure.Sqlite | 事务、文档、真实操作 journal、提交回执和监控持久化 |
| Ipc／Agent.Client | 类型化 App–Agent 协议、请求与事件连接 |
| App | WinUI、输入、选择、未提交草稿、预览和呈现；不写核心 SQLite |
| Agent | 唯一真实执行与数据库写入方、采集／监控会话、生命周期与恢复 |

结构阶段由 Agent 编排，模拟预览与 Agent 提交复用 Application 中的无 IO 规则。旧比较采集链及空 `WinPool.Inventory` 项目已退出；不维持两套生产 collector、模拟 executor 或 App 结构接力。保持现有依赖方向，优先在已有项目调整职责，不增加通用 workflow、DSL、插件平台或公共 SDK。

## 事实、草稿与边界

- `WinPoolFacts` 保存来源、时间、类型、读取状态和关联；`WinPoolSystem`／`StorageSnapshot` 是派生视图。图形布局、管理表和导航不另存可修改关系。显示用假池／假层不进入来源或真实命令。
- 内部稳定 ID 与 provider locator 分工；名称、盘符、DiskNumber、顺序不是持久身份。未知、失败、不支持、否、零和成功空集合分别表达，不用默认值制造可写或无保护角色。
- 关系只按准确唯一证据组合。分区联合体、派生对象和字段来源详见[UnifiedModel](Reference/UnifiedModel.md)，不在此复制其字段表。
- 显示合并缓存可以保留旧事实；真实临写事实必须新鲜且覆盖本次准确目标闭包。局部删除只依据完整成功覆盖域中的 absence；迟到结果不能覆盖新代事实。共享 Primordial 不扩大到无关兄弟盘。
- `SimulationEditingSession` 以 document、baseline revision、BindingGeneration、ContentRevision 关联一次完整预演；先补齐最终 typed edits，再预演。创建 ID 和时间在意图内冻结，App／Agent 候选使用同一规则；相同进度不触发拓扑重建。草稿与提交结果分开，外部改动保留草稿并报告冲突，传输失败不等于未提交。预览不能把本机来源重种成模拟来源。
- 校验放在外部输入、持久化读回及真实临写边界，边界内复用类型化结果。不要每层重复 `IsValid`、hash、编码再解码或日志再抛出。保留实质事务、身份、权限和 Unknown 语义。
- 真实结构目标忽略分区／卷可用空间的运行波动，仍比较格式、标签、盘符、几何和对象增删。持久化往返按字符串值和 Returned null 语义计算指纹；缺失、未采集和失败仍分别保留。
- `InvalidDataException` 不属于 `IOException`；连接超时、无效文档和保存失败由拥有该操作的异步边界呈现一次，其余层不静默吞错。

真实结构 Apply 接受一个冻结目标的 job，以 jobId 幂等，父作业与子 operation 关联在子调用前耐久。原草稿的动作、参数和目标关系摘要由 App 提交，Agent 以 fresh 事实重算后核对，确认前回显同一摘要；外部新增对象不能成为隐含删除目标。整个作业持有真实写入调度所有权；MAX、GPT、MSR 和分区布局仍按阶段推进，新身份和容量只能从 Verified 后态取得。普通单写复用临写上下文和已验证后态，Accept 不重采，正常成功不进入恢复对账。三次采集计量仅适用于非 MAX、一个 Windows 写调用且首次后态成功的路径；补采与 MAX attempt 另计原因，不将整个结构作业算作三次。

写调用前先耐久记录 `CallIssued`；结果未知时不重放。Query／List 只读状态，显式 Recheck 才发起一次只读重查。启动取消未接受的 Prepared，将中断的 Accepted／Running 置 Unknown，并对需核对的结果做一次只读恢复；不自动继续剩余阶段。新计划不用原生 MAX，历史持久计划的只读识别仍保留，不能重解释成新算法。

## 当前数据与通信

| 来源 | 写入方与用途 |
| --- | --- |
| `app-settings.json` | App；语言、主题、页面偏好 |
| `agent-settings.json` | Agent；监控、采样、自启、7z 路径；App 经 IPC 修改 |
| `winpool.db` | Agent；来源文档、模拟系统、工作区选择、结构 job、操作计划／步骤／事件／提交回执 |
| `monitoring.db` | Agent；监控会话、样本及编辑缺口 |
| `MonitoringArchives` | Agent；封存库、压缩包、恢复账本，不是历史浏览库 |

标准数据根为 `%LocalAppData%\WinPool`，便携位置为用户明确选择的程序旁 `Data`；位置指针为 `%LocalAppData%\WinPool.storage-location.json`。切换根前停止相关写入、等待后台任务，保留已提交 WAL 数据和可恢复原件；恢复路径限定目标根，不继续写旧根。允许重建开发数据不等于允许自动抹除未知目录。

内部格式：核心 SQLite **20**、监控 SQLite **2**、IPC **15**、StorageSystemDocument **3**、SourceFacts **1**、StorageSnapshot **3**。这些不是产品版本。核心沿 17→18→19→20 迁移，监控保留 1→2 迁移；不支持的格式明确拒绝。核心 20 增加结构 job 持久化，并解除新模拟回执对 `operation_plans` 和历史整文的依赖；迁移保留旧 commitId 查询与真实 journal、未知结果屏障。

正常模拟编辑只发送 documentId、expectedRevision、commitId 及冻结的 typed edits；Agent 执行共享规则，以同事务 CAS 保存文档和轻量回执。同 commitId 同意图返回原回执，异意图拒绝；回复丢失先 lookup 原 commitId。App 核对回执的 revision／hash 后发布已有候选，不一致或候选缺失时按 ID 读权威正文一次。导入／创建／按需加载仍可传正文。目录以 metadata 加载，选中、导出或编辑时才取所需正文；迟到的切换结果不能覆盖当前选择。

控制请求 pipe 和事件 pipe 分开，请求配对锁保留。帧上限 4 MiB、模拟正文上限 3 MiB；最终 wire budget 在 `IpcFrameCodec` 统一检查，超限请求在发送首字节前拒绝。App／Agent 严格同协议升级，不维持双生产协议。未知提交先查原 commitId／OperationId，不换 ID 重发。

inventory 正文由更新事件唯一承载，手动请求返回 documentId／revision／generation 回执；同 revision 只应用一次，事件缺失或断流后补读一次最新缓存。真实 Prepare 返回冻结计划，接受／查询及状态事件使用递增 revision 的轻量状态，详细 evidence 有界按需读取。工作区持续订阅恢复操作的状态，按持久 revision 去旧；终态先刷新事实及草稿，再解除提交屏障，关闭时取消订阅。控制与事件连接的独立 5 秒超时由连接拥有者归一为失败结果，UI 操作边界呈现一次。

监控在 coordinator 接受样本时分配 sequence，再经过可能丢样的有界队列；Agent 传输最多等待 250 ms、每批最多 128 点，按实际 wire 上限继续拆分，独立于 SQLite 写入批次。App 一个 cache 供图表和告警，以实例／session／sequence 去重；快照和状态共用 Agent 实例内的单调 revision，迟到状态不能清空已恢复窗口。初连／重连或 gap 同步窗口，未知区段不画成连续曲线。稳态不轮询完整 Agent snapshot，告警也不另起轮询。

采样、传输和绘图频率分开，20 Hz 设置保留；正常窗口淘汰不计为持久化丢样。真实编辑缺口保留原 operation/step、计数器族和开始时间，只有对应实际样本恢复才结束，不为停止／重启补造历史端点。轮换账本使用根内相对路径，完整性与内容校验后才释放封存原库，失败保留有效数据。

## 启动、退出与采集

App 要求配套 Agent connection，生产 MainWindow 不再回退到本地 SQLite、采集或监控。启动可先以只读 SQLite 快照预显所选系统；不取得写租约、不迁移数据库、不将历史呈现为新采集。Agent 连接后读取 metadata 和所选正文、确认工作区并采集。页面只由用户偏好 `LastActivePage` 恢复；workspace 仅存系统／对象选择。

完整／Storage／scope 采集使用一份程序集内固定模板，以封闭 purpose/scope／discovery 参数经 stdin 执行，不发布独立 inventory `.ps1`。普通读取默认关闭 discovery；需要发现时显式开启，发现失败不阻断独立物理盘枚举。缺物理事实时不进入 extent 路径。Agent 的执行、inventory 和 monitor 共用 collector；本机绑定、generation 和完整 coverage 相符才复用原始事实，临写推进新代，monitor 可复用完整的 Verified 后态。稳定系统绑定缓存不替代实时设备身份／角色／能力。布局与容量查询保留各自语义，不以 provider 上界作为已预留容量。

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

`build/Package-WinPool.ps1` 复用已验证的独立树与现有 merge，不运行 Rebuild 或启动程序。它核对 App／Agent 版本、必需资源、两侧与标准树的完整 runtime 文件集和 SHA-256；输入夹带 Data、数据库、日志、诊断、证据或脚本时拒绝打包。标准树的开发 Data 不遍历／复制。使用 `-SkipPdb`、全新 `artifacts/packages/<版本>/<UTC+guid>/WinPool-<版本>-win-x64`，复核合并结果，再生成中英 `TESTING.md`、zip 和 SHA-256。资源、`.pri`／`.xbf` 及 7-Zip 许可证／NOTICE 保留，现有 merge 路径保护不放宽。

```powershell
.\build\Package-WinPool.ps1 -TestedScope '填写本批次实际通过的验证范围'
```

说明记录真实 Git HEAD／dirty 状态；没有填入的实测范围明确记为未登记。脚本存在不表示候选包已生成或已验证，实际结果归 Quality／Plan。

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

唯一版本源为 `Directory.Build.props`。本轮收口使用 `WinPoolVersionMajor=0`、`Minor=6`、`Iteration=1`，由既有公式生成 V0.61／0.6.1／0.6.1.0；文档目标不替代实际版本字段。全部本轮实现属于同一 V0.61，不为工作包单独升版，不创建迭代 10。V0.68／V0.69 仅在未来确需迭代时收紧范围，首稿不为用完版本号等待。
