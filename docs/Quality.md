# WinPool 验证与验收

日期：2026-10-07。V0.58 本机真实磁盘修改第一阶段已完成。逐阶段操作账本和历史结果保留在忽略提交的测试证据目录；详细最终 H11 结果见 [H11 检查点](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/runRoot/H11-final-closeout-checkpoint-20261007.md)。收口前 Quality 原文保存在 [Acceptance-history.md](Archive/20261007-real-edit-stage1/Acceptance-history.md)，不作为当前状态。

## 最终验收

- **现场序列：** H00 基线及 H01–H11 适用操作已完成。包括分区/GPT/MSR、NTFS/exFAT/ReFS、EFI/Recovery、RAW 扩缩与删除、盘符/卷标、脱机/联机、建池、VD 与 HDD tier template、自动布局和分阶段重建。C03 因目标盘原已为 GPT 而 `not_required`；C05 现有 VD 与实际层实例扩展仍按边界条件禁用；D 类能力保持拒绝。
- **C02 与末态：** ReFS 1 GiB/65,536-byte cluster 卷扩至 2 GiB 后，同一 16 MiB 测试文件 SHA-256 保持一致；Shrink 属性及 UIA 均为禁用，候选卷随后删除。最终 WDC 为在线 GPT，16 MiB MSR 与 MAX NTFS/65,536-byte cluster BasicData，卷标 `WinPool_Test`、E:。
- **保护盘：** H00 基线到最终布局、普通重启的 Samsung scoped comparisons 均为 0 changes、0 evidence gaps。范围仅限两块 Samsung 的关联结构；它排除有正面 `IsPrimordial` 证据的共享 aggregate 字段和非目标成员边，不代表全机结构无变化，也不能排除 Windows 后台 I/O。
- **工程门：** 稳定 V0.57 源码门共 12 个项目、1,450 项：1,447 Passed、0 Failed、3 NotExecuted（既有手工 archive/performance measurements）。Release 构建 0 warnings、0 errors；23 项依赖审计未报告漏洞。T01–T16 交叉表中的所选方法均在最终 TRX 中找到并 Passed。版本源随后提升为 V0.58；App/Agent 标准重建 0 warnings、0 errors，两个程序集 metadata 与 About UIA 均显示 V0.58。完整测试门没有在单独的版本号提升后重跑。
- **P5 与正常重启：** 原生 UAC 提权交接和 20 Hz monitoring 通过，交接期间 accepted 与 call_issued 未增长；普通启动 Real Off，App/Agent 正常退出。Application 日志查询得到 36 条 1000/1001/1026 候选，精确 WinPool 消息匹配为 0；这不确定历史 E_POINTER 的根因。

## 证据边界

- 最新严格目标审计保留 3 条旧记录缺少 `PhysicalMemberObjectId` 的字段缺口；每条仍保存准确 WDC UniqueId 和 serial，历史行未回填。
- 另一条历史 format-volume 失败步骤的 `target_json` 缺失，且该步 `call_issued=0`。有 preflight-failed 事件，但没有显式持久化 `NoWindowsCall=true`；因此不能把它改标为身份完整或已证明的 NoWindowsCall。最终审计原报告未被放宽或改写。
- `call_issued` 记录的是应用层发起边界，不单独证明 Windows 存储变更确已发生；历史目标字段完整性按严格审计结果保留。
- Persisted-inventory helper 不证明具体一次 ScanAsync 的因果；Samsung comparison 也无法排除后台 I/O。
- 最终门之后的版本号提升只由标准构建、metadata 和 About UIA 核对，不等同于完整测试门重跑。

最终门和 H11 目标审计位于 [本次测试证据](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/)。完整 H11 结构、操作 ID、几何、文件哈希与退出证据见 [H11 最终检查点](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/runRoot/H11-final-closeout-checkpoint-20261007.md)。本阶段已归档；后续工作需另立范围和验收计划。

## 选择验证范围

- 纯文档任务：检查内容一致性、当前链接、归档完整性及 Git 范围；不运行代码、原生、设备或视觉测试。
- 普通修复/小功能：运行能验证风险的直接相关检查，不自动扩成全套验收。低影响、可逆修改不为凑数量添加测试。
- 执行已确认 Plan：其中明确要求的回归和自动门属于任务本身，无须重复申请；阶段收口运行 Plan 指定范围。
- 完整人工、平台、设备和发布验收：仅在用户明确要求或已确认计划明确包含时进行。完成代码不自动表示完整人工验收开始或结束。
- 发现失败先判断是实现缺陷还是已失效的旧约定；不能删除有效安全断言来让测试通过，也不能让旧断言恢复用户已经否定的行为。

结果只能使用 `passed`、`failed`、`unverified`、`not_required`、`deferred_by_user`。跳过、缺少环境和未运行均不能报 passed；源码推断与实测证据要分开。

## 文档与架构检查

- 当前内部文档为中文单一权威，仅根 README 成对维护；历史副本原样保留，不要求归档一律配对或翻译。
- 有活动阶段时只有一个 `docs/Plan.md`。用户要求保留的待执行计划可继续留在该文件中，但须明确标记“未激活”，不得当作当前实施授权。具体激活状态以 Plan 为准。Design 不被枚举为待执行计划；标题或旧正文中的命令式措辞不改变其状态。
- 检查当前文档链接和路径、当前版本与目标版本的区分、已实现与待验证的区分。
- 历史文档的原相对链接按归档说明追溯，不为修历史链接重写当前规则。
- 保留真正的依赖、单写入方、类型化命令与默认拒绝边界测试。文档检查验证当前约定，不依赖整段固定句子或把所有旧文件数量当成永久产品要求。
- Git 排除生成物、数据库、日志和源图，保留软件实际消费的资源。

内部文档为中文单一权威，仅根 README 成对维护。该约定由架构测试 `CurrentInternalDocumentsAreChineseAuthoritativeAndOnlyRootReadmeIsPaired` 固定。

## 存储语义与提交检查

优先使用固定输入、纯函数规则测试和实际应用服务集成测试；不以扫描源代码字符串代替行为验证。

- 已知合法、已知非法、信息不足都覆盖；未知不能被默认值转换为允许。
- 原始容量、逻辑容量、占用和可用范围分别检查；覆盖不同冗余、边界、对齐、溢出和估算来源。测试值不是 Windows 实测证据。
- 采集 → 转换 → 保存 → 加载保持身份、用途、层参数、卷和挂载点；缺失值与采集失败仍可辨认。
- 对象关联和关系投影一致；创建、删除、成员变更后无悬空引用、重复身份和错误归属。
- 预览与提交使用同一操作序列；拒绝时不产生部分模拟提交；修订冲突不覆盖其他修改。
- 覆盖“提交成功但回复丢失”，验证结果未知和持久化对账，不能仅断言抛出了异常。
- 能查看 Windows 结构不代表已验证相应修改操作。只读采集样本不能冒充真实写操作验证。

## 监控持久化与归档验证口径

监控轮换阶段的具体场景归[阶段计划归档](Archive/20260921-monitoring-rotation/Plan.md)，以下规定后续回归的证据口径，不自动赋予未运行的场景通过结论：

- 连续性测试必须在切换期间持续产生样本，并覆盖多次轮换；按样本身份或完整值的多重集核对，不只比较总条数。多个设备共享时间戳、同库多个会话、NULL 与零都须可区分。
- 故障测试验证最终数据和文件状态，不只断言抛出异常；覆盖旧库改名前后、新库初始化及归档发布/原库释放边界。重启不能以新建空库掩盖唯一有效旧库失联。
- 数据根迁移需证明恢复记录只引用目标根内的文件，未在旧根继续压缩、写入或释放源库；归档恢复以完整性和内容校验为证据，不以文件存在代替。
- 验证有界缓冲和后台故障时，同时检查实时采样、实际持久化、已知未保存数量与用户诊断的一致性。正常窗口淘汰不能计入持久化丢样。
- 实际 7z 测量记录输入数据、固定参数、原始/归档大小、耗时及峰值资源；小阈值故障测试不能代替生产 1 GiB 阈值与完整归档验证。构建成功不能代替这些行为测试。

## 自动门

正式阶段如 Plan 要求，在 WinPool 仓库根运行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

自动测试使用各自夹具规定的数据，不修改真实存储结构。开发阶段可直接关闭 WinPool 进程、修改或重建已核实的 WinPool 开发数据，构建输出默认写入标准 `artifacts/Release`；不为保留旧运行实例额外搭建隔离产物或重复测试。记录实际命令、提交基线和结果；警告逐项说明，未解决失败不能作为完成。

App 和 Agent 独立产出、SHA-256 并集合并与碰撞失败机制继续验证；运行时查找与目录布局必须一致。命名管道身份/ACL、SQLite 所有权、原子提交及只读边界仍受直接回归保护。

## 人工与设备证据

WinPool 是原生多进程应用。浏览器 DOM 测试不替代 WinUI、托盘、原生选择器和设备检查。

需要实际证据的项目包括页面与拖拽、软件中英切换、主题/DPI/高对比度、键盘操作、托盘生命周期、文件夹选择器、监控启停和数据位置往返。1.x 的测试页验证路线占位及可访问性；当前开发页显示有界内存消息，验证其导航门、详情复制和退出后清空，不据此开发日志文件查看器或完整工作区。

模拟规则验证不执行真实存储写操作。真实写入须沿用 Product/AGENTS 已确认的准确授权，并记录当次目标与结果；自动测试和 CI 不触碰真实结构。缺少设备或目标平台时明确未验证，不制造通过记录。

自动门证明工程行为，不能批准视觉意图或物理设备行为。用户接受阶段也不能把未执行用例改写为 passed。例外记录原因、范围、批准者和风险，保持简短可追溯。
