# V0.2 外部工具测试、监控与 SQLite 计划

## 1. 目标

V0.2 不把 Dite 当作唯一外部边界，也不在 C# 或 Win32 中重新实现 DiskSpd、
RoboCopy、fio、RAMMap 等成熟工具的核心能力。新测试子系统吸收 Dite、KS、WEMigration
已验证的产品思想：

- 预设即用；
- 配置驱动任务；
- 真实工作负载和基准工作负载并存；
- 多步骤调度；
- 缓存语义明确；
- 速度、IOPS 和延迟同时记录；
- 重复测试独立聚合；
- 生成、复制、验证、清理和证据归档；
- 中断、恢复和可审计状态。

WinPool 负责类型化测试定义、任务调度、工具发现、参数生成、进程隔离、输出解析、
指标归一化、恢复、证据和 UI。实际 I/O 与复制通过 DiskSpd、RoboCopy、fio
及后续登记的外部工具执行；系统缓存/standby list 清理由 RAMMap 外部工具执行。

外部工具不随 WinPool 发布。Settings 提供检测、安装按钮和自定义路径。用户确认安装
后，WinPool 先以当前进程权限完成可行的安装；只有目标位置或安装器确实要求管理员
权限时，才通过一次性提权 Broker 完成安装。开发阶段优先使用本机现有安装；缺失时
可以由开发者确认后安装。

## 2. 测试页完整功能

Test 页目标布局保持 WinPool 现有视觉语言，包含：

1. 测试目标区
   - 系统和卷；
   - 环境；
   - 只读/写入能力；
   - 剩余空间和预计写入量；
   - 当前机器存储结构保护和测试目录范围。
2. 预设与任务区
   - 内置预设；
   - 自定义测试定义；
   - 每个步骤、参数和预计时间；
   - 算法置信度。

自定义预设现通过当前用户 Agent 的封闭命名管道保存到 SQLite `preferences` 命名空间，
而不是写在主界面进程的本地临时状态中。预设可新建、更新、加载和经确认移除，保存
场景、工具、验证方式、混合文件数、访问模式、写入比例、文件/块大小、线程、队列、
预热、持续、冷却、重复次数及延迟采集设置。Agent 和 repository 都验证数值范围和
工具绑定，只有持有单写 lease 的 Agent 能修改。目标目录、卷身份、RAMMap、调度、
临时电源计划和批次 Flush 等一次性/提权选择刻意不进入预设；加载后必须重新选择目标、
审阅系统操作并生成新的不可变哈希计划，避免“加载预设”等价于隐式授权。
3. 计划审阅区
   - 目标、风险、缓存模式；
   - 文件大小、块大小、线程、队列深度和时长；
   - 写入量、验证、清理和取消边界。
4. 运行控制
   - 开始、暂停能力说明、取消；
   - 当前步骤、总体进度、剩余估计；
   - 不支持真正暂停的步骤必须明确显示。
5. 实时指标
   - MiB/s、IOPS；
   - P50/P95/P99/P99.9 延迟；
   - 活动率、队列、CPU；
   - 读写量和错误数。
6. 历史与比较
   - 按磁盘、预设、时间和配置筛选；
   - 多次运行叠加；
   - 同一指标的统计比较；
   - 明确标记硬件、系统和算法版本差异。
7. 证据与导出
   - CSV、JSON、Markdown 摘要；
   - 测试定义、环境快照和算法版本；
   - 附件 SHA-256；
   - 失败和取消仍保留部分证据。

进度通道采用独立 Agent 事件管道，不把异步帧混入控制 request/reply。fio 使用
`--eta=always --eta-interval=1s` 保留原生 ETA 百分比；RoboCopy 不使用 `/NP`，保留
自身百分比。Agent 对 TestWorker 的有界 stdout/stderr 批次做流式百分比提取和 250 ms
节流，只向 App 发送运行 ID、步骤 ID、固定代码、状态和 0–1 比例，原始文本继续作为
证据保存但不进入 UI 事件。DiskSpd 等没有原生百分比的工具只发送开始、结束和终态，
不得用伪造百分比冒充工具进度。快照轮询保留用于监控指标及事件断线后的最终状态补偿。

当前机器允许：

- 在明确选择的测试根目录创建运行子目录；
- 创建、写入、读取、校验和清理本次运行登记的测试文件；
- 使用 DiskSpd、RoboCopy、fio、RAMMap 和其他已配置工具；
- 执行计划中的临时文件清理、RAMMap 系统缓存/standby list 清理、卷
  Flush、TRIM/Optimize；
- 调整测试进程的优先级和 CPU affinity；
- 临时切换电源计划并在结束或恢复阶段还原；
- 模拟、历史回放和导入结果分析。

Test 页必须持续显示测试根目录、预计写入量、工具、辅助操作和清理范围。
禁止原始设备写入以及初始化、格式化、分区、建池等存储结构修改。

## 3. 测试定义模型

```csharp
internal sealed record TestDefinition(
    TestDefinitionId Id,
    string Name,
    string Version,
    IReadOnlyDictionary<string, TestParameter> Parameters,
    IReadOnlyList<TestTaskDefinition> Tasks,
    IReadOnlyList<TestScheduleStep> Schedule,
    AlgorithmConfidence Confidence);
```

计划动作至少覆盖 Dite 当前语义：

- `CheckSpace`
- `GenerateFile`
- `RunIo`
- `Copy`
- `Repeat`
- `Store`
- `Summarize`
- `Verify`
- `Cleanup`
- `WaitForIdle`
- `CaptureHealth`
- `ExportArtifact`

定义文件只描述受支持的类型化动作，不允许命令字符串。

## 4. 外部测试工具适配

### 4.1 工具注册

每个工具登记：

- `ToolId`、显示名称和用途；
- 官方主页和官方安装来源；
- 自动发现规则；
- 自定义路径；
- 支持的版本范围；
- 版本查询参数；
- 可执行文件 SHA-256 和签名信息；
- 支持的测试能力；
- 输出格式和解析器版本；
- 是否需要管理员权限。

首批工具：

- DiskSpd：文件型顺序、随机、混合 I/O，队列、线程、缓存、IOPS 和延迟。
- fio：可扩展工作负载、job 定义和 JSON 结果。
- RoboCopy：真实文件复制、元数据复制和恢复语义。
- RAMMap：测试前后的系统缓存/standby list 清理，仅开放类型化白名单动作。
- 后续工具通过新的类型化 adapter 加入，不修改 Application contracts。

### 4.2 参数映射

WinPool 的 `TestStep` 是工具无关模型。适配器负责把它映射为具体参数：

- 操作类型；
- 文件和工作目录；
- 文件大小；
- 块大小；
- 顺序/随机/混合比例；
- 线程和队列深度；
- warm-up、duration、cool-down；
- 软件缓存和写穿；
- 延迟采集；
- 输出格式。

映射结果必须保存为结构化参数数组和本地化预览。参数不支持时在计划阶段拒绝，
不得静默降级。

### 4.3 DiskSpd 适配器

- 按已验证映射生成 `-b`、`-d`、`-o`、`-t`、`-r`、`-w`、`-L` 等参数。
- 缓存参数严格按 DiskSpd 官方定义映射并做单元测试。
- 使用 XML 结果作为主要解析输入，控制台文本只作诊断。
- 保存 DiskSpd 版本、完整参数数组、XML 和 stderr。
- 进程取消时终止整个子进程树并保留部分证据。

### 4.4 fio 适配器

- 使用 WinPool 生成的 job 文件或类型化命令参数。
- 优先要求 JSON/JSON+ 输出。
- 把 fio job、job options、版本和原始 JSON 作为证据。
- 将 fio 的 bandwidth、IOPS、latency 和 errors 归一化到内部指标模型。
- Windows 版本、安装来源和支持参数必须按检测结果决定，不假定所有平台一致。

### 4.5 RAMMap 适配器

- RAMMap 是 Sysinternals 外部测试辅助工具，不随 WinPool 发布。
- 以 Dite 当前使用的 `-Es`、`-Et` 组合作为首个兼容模式；在支持版本样例和
  端到端验证完成前，不扩展其他清理参数。
- Application 只传递 `RamMapCacheClearMode` 枚举；适配器生成固定参数数组，
  不接受 UI 文本、导入内容、数据库值或 IPC 消息中的自由参数。
- 运行前检测路径、版本、签名/发布者和 SHA-256；路径或工具身份变化会使既有计划失效。
- RAMMap 缺失时，功能入口提示用户在 Settings 安装或配置自定义路径。
- 开发阶段默认允许清理；正式产品必须先显示会改变系统缓存和测试条件的警告或确认。
- 因需要管理员权限而提权时，实际执行交给一次性 `WinPool.ElevatedBroker` 的固定
  RAMMap 动作，不向 Broker 传递任意可执行文件路径。
- 保存模式、实际参数、退出码、stdout/stderr、开始结束时间，以及可获得的清理前后
  内存/缓存监测快照；退出码成功但身份或证据不完整时不得标记为完全成功。

### 4.6 工具进程隔离

- 外部工具由按需 `WinPool.TestWorker` 启动。
- 需要提权的 RAMMap 动作由一次性 Broker 启动，并由 Agent 纳入同一次 run 的监督和审计。
- Worker 使用 Windows Job Object 管理完整子进程树。
- 主界面关闭不直接杀死测试；Agent 继续显示状态。
- 托盘退出会请求取消测试、等待证据 flush，并在超时后结束子进程树。
- stdout/stderr 采用明确编码和原始字节备份。
- 工具崩溃不会带崩 Agent 或主界面。

## 5. 数据生成

支持 Dite 的大文件、混合文件和可恢复增量生成思路，但优先组合现有工具：

- 固定大小大文件；
- 多尺寸分布的小文件；
- 目标文件数；
- 目标总字节；
- 顺序或确定性名称；
- 可恢复清单；
- 每文件内容校验信息。

实现来源：

- DiskSpd 负责其支持的测试文件创建和 I/O 内容。
- fio 负责 job 中的文件准备、填充和数据模式。
- Dite/FileGen 当前脚本可以作为过渡工具适配器保留。
- 大量混合文件生成可复用 Dite/WE 已有脚本或经确认的外部生成工具。
- WinPool 负责任务清单、路径边界、恢复状态和证据，不在 C# 中重写大规模数据生成引擎。

混合文件不能伪装成编译期已知的单一登记文件。V0.2 使用哈希绑定的
`RegisteredTestDirectory`：固定相对目录、身份、最大字节数和最大文件数，且只能位于
本次 `WinPoolRuns\<RunId>` 下。Agent 在生成后、复制后分别枚举并拒绝 reparse point、
越界字节数或越界文件数，然后才允许进入下一步骤。RoboCopy 目录模式只接受登记的源/
目标根，固定 `/E /XJ`，不允许 `/MIR`、`/MOVE` 或 `/PURGE`。

Dite 过渡适配调用当前 FileGen 算法的非交互封闭入口，不把生成算法复制到 C#：

- 只接受 `big`/`mixed`、总 MiB、文件数、pool MiB 和登记输出目录；
- 首次输出目录必须为空；断点续作必须携带计划中登记目录的 64 位身份；
- 先原子保存带 SHA-256 摘要的完整文件大小清单，文件大小序列由登记身份确定；
- 每个文件先写同目录 `.winpool-partial`，完整 flush 后再原子命名；
- 续作只复用名称和长度与清单一致的已提交文件；未知条目、链接、冲突长度、身份或摘要不符均拒绝；
- 最后一行必须是 `Dite.FileGenResult` v2 JSON，分别报告最终总量、本次生成量和复用文件数；
- Dite 可执行文件仍不随 WinPool 发布，设置页只提供官方来源和自定义路径；
- 恢复清单本身计入登记目录的哈希绑定字节/文件数配额，Agent 仍在步骤完成后做独立边界检查。

每种工具的数据模式、稀疏/预分配行为、压缩可能性和实际写入量必须随结果记录，
避免把逻辑文件大小当成真实写入量。

恢复算法：

1. 为每个文件保存计划大小、生成工具、工具参数、状态和已确认长度。
2. 恢复时重新检查路径、大小和文件身份。
3. 仅在最后一个完整块边界继续。
4. 不符合清单的现有文件不覆盖，标记冲突。
5. 当前机器允许在授权测试目录中执行。

## 6. RoboCopy 与外部复制测试

主要实现：

- RoboCopy：Windows 内置复制引擎和正式文件复制测试。
- fio：适用于其支持的复制/I/O 工作负载。
- WEMigration：保留其恢复、批次、状态和证据设计作为参考或过渡适配器。
- 其他复制工具后续通过同一 adapter contract 加入。

复制模式：

- `/COPY:D`；
- `/COPY:DAT`；
- 经明确审阅的其他模式。

RoboCopy adapter 还必须显式控制：

- `/J` 缓冲语义；
- `/MT` 线程数；
- `/R`、`/W` 重试；
- `/XJ` 防止 junction 穿越；
- 日志文件和控制台编码；
- 可接受的退出码范围。

V0.2 已实现 `ALG-COPY-BATCH-001` 1.0.0（`Derived`）作为 WEMigration
恢复语义的内部实现：

- Agent 在首次复制前按不区分大小写的相对路径稳定排序，以计划中的字节阈值和
  最大文件数形成不可变批次；超大单文件允许单独形成一个批次；
- 文件相对路径、长度、最后写入时间、属性、可选 SHA-256、源/目标登记目录身份、
  测试计划哈希和算法版本共同进入 manifest，并生成独立 SHA-256；
- manifest、批次和逐文件 `Pending/Copying/Completed/Failed/Conflict` 检查点由
  Agent 在 SQLite 单事务保存，不能用同一 run/step 覆盖为另一份 manifest；
- Agent 非正常结束后把开放批次标为 `Interrupted`，把未完成的 `Copying` 条目退回
  `Pending`；恢复只接受原计划哈希完全一致且用户再次确认的运行；
- 恢复前重新枚举源和目标。源元数据变化、目标冲突、未知目标条目均拒绝；只有
  长度、时间和属性完全一致，且 manifest 带哈希时哈希也一致的目标才可接受为完成；
- RoboCopy 的 0–7 按其位掩码语义视为可接受，8 及以上失败；可接受退出码后仍必须
  通过目录边界检查和上述逐文件恢复判定；该退出策略由 Agent 与 TestWorker 共用，
  因此 1–7 不会误使 Worker 跳过同批后续请求。
- RoboCopy adapter 已能把 manifest 中的单个相对文件安全投影为“对应源父目录、对应
  目标父目录、字面文件名”的外部调用，拒绝 rooted、`..` 和通配符路径；这是后续
  逐批执行器的封闭参数边界。
- `CopyBatchInvocationPlanner` 会核对 manifest、step、工具身份和完整 checkpoint 集，
  跳过 `Completed`，只把 `Pending/Failed` 条目转换为类型化请求；`Conflict`、
  `FailedFinal` 或遗留 `Copying` 不会被静默重放。SQLite 可在单事务中仅把本次最多
  512 个明确 ordinal 标为 `Copying` 并递增 attempt，未调度的后续批次保持 Pending。

当前执行端已按持久化 batch 顺序执行；每个 batch 再按最多 512 个明确 ordinal 形成
有界 dispatch，每个 manifest 文件映射为一个外部 RoboCopy 进程。所有 dispatch 都走
与普通测试相同的 Agent 进程登记、独立 TestWorker、Job Object、调度恢复、取消和托盘
退出监督路径。每组结束后按进程 PID 分离输出，保存原始证据并重新枚举整个源/目标；
只有本组条目全部成为 Completed 才进入下一组，失败退出码保存为 Failed，未返回条目
回到 Pending。混合目录复制可显式选择默认关闭的批次间 Flush；该动作、目标卷 GUID
快照和显示路径均进入不可变计划及其哈希，并且编译器和 Agent 都拒绝把它用于非目录
复制计划。每个非末批验证完成后，Agent 通过一次性提权 Broker 重新解析当前卷 GUID，
只在与计划快照一致时执行 `FlushFileBuffers`，保存审计和带哈希 JSON 证据；该选项可能
每批显示 UAC，界面必须在开始前明确警告。随后执行 `ALG-SETTLE-001`：要求存在活动
Agent 监控会话，
只使用同时具有活动率、平均队列、读写吞吐且不陈旧的目标；所有目标连续 3 秒低于
5% 活动率、0.25 队列和 1 MiB/s 合计吞吐才进入下一批，60 秒超时即失败。等待时长、
最终最大活动率/队列/吞吐按 batch 写入指标。自动故障注入已证明：TestWorker 被强杀
时 Job Object 会回收仍运行的外部进程；模拟 RoboCopy `ERROR 112`/退出码 8 会保留
stderr、停止后续 dispatch，并由现有 SQLite 恢复路径把开放 `Copying` 条目退回
`Pending`。真实受控卷填满和超大目录验收仍是后续门槛。实际字节复制始终由 RoboCopy 完成，不在
C# 中重写复制引擎。

规模侧已有 50,505 条倒序输入的纯计划测试，验证不区分大小写稳定排序、512 文件上限、
99 个确定性批次和 manifest 哈希；这证明算法与计划边界，不替代兼容 Dite 构建、真实
RoboCopy 长运行及资源占用验收。

指标：

- 实际读取和写入字节；
- 总耗时、首字节和尾部 flush；
- 源/目标吞吐；
- 队列和活动率；
- 元数据文件数；
- 重试和错误。

不得只依赖文本日志判定成功。成功至少要求工具退出码、目标存在、大小和所选验证策略一致。

小文件并发度自动调节属于 `【推测，待验证】`；在对照试验完成前使用显式固定并发。

## 7. 缓存模式

将 Dite 对缓存语义的经验转为强类型：

```csharp
internal enum SoftwareCacheMode
{
    Enabled,
    Disabled
}

internal enum WriteThroughMode
{
    Disabled,
    Enabled
}
```

计划中显示实际工具参数，不能只显示“缓存开/关”。

- DiskSpd 的 `-S` 参数族按官方定义映射并自动测试。
- fio 使用对应 engine 和 direct/sync 等明确参数。
- RoboCopy 使用 `/J` 表达无缓冲复制。
- 硬盘设备自身缓存未被工具可靠控制时必须显示“未控制”。
- RAMMap 系统缓存/standby list 清理与删除临时文件是两种独立操作和能力，不得互相隐式授权。
- RAMMap 只执行适配器登记的固定清理模式；首个模式沿用 Dite 的 `-Es`、`-Et`
  基线，并将实际参数显示在计划审阅与证据中。
- 开发阶段默认允许 RAMMap 清理；正式产品每次或按明确设置警告/确认，且不能静默清理。
- 清理前后尽可能保存系统内存、缓存和 standby 状态快照；无法获得时标记证据限制。
- 不把“刚创建的文件”默认宣称为冷缓存。
- 缓存状态不确定时，结果必须带限制说明。

## 8. 指标算法

### 8.1 吞吐和 IOPS

```text
ThroughputMiBps = completedBytes / measuredSeconds / 2^20
IOPS = completedOperations / measuredSeconds
```

优先采用工具结构化输出中的测量窗口和指标。WinPool 重新计算值时，必须同时保存
工具原值、重新计算值和差异，不能静默覆盖。

### 8.2 延迟

延迟优先使用 DiskSpd XML、fio JSON 等工具提供的分布或统计。

输出：

- 最小、最大、平均；
- P50、P90、P95、P99、P99.9；
- 标准差；
- 样本数；
- 直方图边界和溢出数。

工具提供直方图时保存原始桶并归一化；只提供分位数时不得伪造完整直方图。

若需要合并不同工具的直方图，必须先统一单位和桶语义，并保留原始分布。

### 8.2.1 跨工具语义身份

`ALG-METRIC-003` 1.0.0 为原始工具指标附加语义身份，不覆盖或改名原值。身份包含
canonical metric id、canonical unit、完整 workload key、聚合意图、是否可跨工具比较
和限制代码。DiskSpd 与 fio 只有在读写方向、顺序/随机、块大小、队列、线程、软件缓存、
write-through、单位和 canonical id 全部一致时才允许进入同一比较组；`MB/s` 不会被静默
当成 `MiB/s`。RoboCopy/Dite CopyCross 的复制吞吐与块 I/O 基准吞吐分属
`throughput.copy` 和 `throughput.read/write/mixed`，不得混用。

Dite 的 `Read/Write_SEQ/RND...` 表头会解析出方向、模式、块、队列和线程，但旧汇总缺少
可靠的缓存/预设上下文，因此只显示 canonical 映射并标记
`metric.legacy_cache_profile_unknown`，不会宣称可与现代运行直接比较。旧数据库中尚无语义
字段的 Dite 汇总在 Agent 查询时动态补齐映射；UI 同时显示原 metric id、原单位、canonical
identity、workload key 和限制代码。

### 8.3 重复聚合

每次重复保留完整独立结果，再生成聚合：

- 吞吐、IOPS：默认中位数，同时显示最大和最小。
- 延迟：默认使用各运行 P99 的中位数，不能把不同运行的 P99 简单平均后假装为总 P99。
- 若保留全部直方图，可合并直方图后重新计算总体分位数。
- Dite 的“速度/IOPS 取最大、延迟取最小”作为 CrystalDiskMark 行为兼容预设，不作为通用默认。

### 8.4 异常值

- 不静默删除异常值。
- 明确记录系统干扰、采样缺失和限速。
- 可提供 MAD/IQR 标记，但标记不等于排除。
- 自动判断稳态区间属于 `【推测，待验证】`，结果必须同时保留原始窗口。

## 9. 验证算法

验证级别：

- `Metadata`：存在、大小、时间、属性。
- `SampledContent`：确定性分块抽样。
- `FullHash`：完整 SHA-256。
- `PatternReplay`：按种子重新生成并逐块核对。

抽样计划必须由文件 ID、大小和测试种子确定，保证可重放。所有风险文件、重试文件和中断恢复文件强制升级为完整验证。

对于任意外部工具产生的源/目标复制对，WinPool 只提供 `Metadata`、由双方登记身份和
长度确定的可重放 `SampledContent`、以及双端 `FullHash`。`PatternReplay` 仅用于
带确定性生成种子和恢复块信息的登记文件；不得从 DiskSpd、fio、RoboCopy 或导入结果
中臆造生成模式。复制验证不替代 RoboCopy 的退出码判断，两者必须同时成功。

哈希流水线继续吸收 WEMigration 的经验：

- 源、目标和比较分离；
- 进度可恢复；
- 结果不依赖外部命令文本；
- 每个实际哈希记录读字节、耗时和速度；
- 比较结果以稳定文件 ID 关联。

## 10. 清理和证据保留

- 测试文件必须位于执行计划创建的专用目录。
- 清理只能处理本次计划登记的文件身份。
- 取消或崩溃后不自动扩大清理范围。
- 清理前再次核对根目录、文件 ID 和计划清单。
- 失败证据和日志先归档，再执行用户批准的运行时清理。
- 当前机器可以清理本次运行登记的测试文件。

## 11. 独立 Agent 中的 Monitor

保留 V0.13 外观和行为：

- 物理盘与虚拟盘；
- 60 秒曲线；
- 活动率、读、写；
- 每盘颜色、选择和表格；
- 0.2/0.5/1/2/5/10/20 Hz；
- 后台监控开关；
- 自动开始、停止、CSV 导出。

新管线：

```text
WinPool.Agent (tray)
  -> PDH/other monitor source
  -> timestamp normalization
  -> bounded channel
  -> in-memory latest window
  -> SQLite batch writer
  -> optional CSV exporter

WinPool.App
  -> named pipe query/subscription
  -> UI projection
```

- 监控采样、会话状态和 SQLite 写入不在主界面进程。
- 关闭主界面后 Agent 和托盘图标继续存在，监控按用户设置继续。
- 重新打开主界面后通过命名管道恢复最新窗口和会话状态。
- 从托盘选择退出会停止监控、flush 数据库并完整退出全部 WinPool 进程。
- Agent 无法创建托盘图标时不得静默继续运行。
- 采样线程不等待 SQLite。
- 有界通道满时不阻塞 UI；记录丢样数量并发出警告。
- 数据库写入失败不终止内存显示，但会标记会话证据不完整。
- 主界面窗口最小化或关闭不决定 Agent 生存期。
- 活动率仍限制在 0–100%，同时保留原始值用于诊断。

## 12. SQLite 设计

### 12.1 数据库设置

- SQLite 单文件：`<data root>\winpool.db`。
- `PRAGMA foreign_keys=ON`。
- `journal_mode=WAL`。
- `synchronous=NORMAL` 作为高频样本默认值。
- 设置 `busy_timeout`。
- Agent 作为 SQLite 主写入进程；其他进程通过命名管道提交写入事件。
- 每个读进程一个连接工厂，不跨线程共享可变连接。
- schema 版本保存在 `schema_info`。
- 所有时间保存 UTC 整数微秒或毫秒，并记录采样时钟来源。

### 12.2 核心表

```text
schema_info
preferences
workspace_state
systems
inventory_snapshots
storage_objects
storage_relationships
operation_plans
operation_steps
execution_events
monitor_sessions
monitor_devices
monitor_samples
monitor_rollups
test_definitions
test_runs
test_steps
copy_batch_manifests
copy_batches
copy_batch_entries
test_metrics
latency_histograms
legacy_test_imports
legacy_test_runs
legacy_test_metrics
artifacts
algorithm_registry
inventory_comparisons
external_tools
tool_install_events
agent_sessions
worker_processes
```

### 12.3 高频监控表

`monitor_samples` 至少包含：

- `session_id`
- `device_id`
- `timestamp_utc`
- `activity_pct`
- `read_bytes_per_sec`
- `write_bytes_per_sec`
- `queue_length`
- `sample_flags`

主索引：

- `(session_id, timestamp_utc)`
- `(session_id, device_id, timestamp_utc)`

写入策略：

- 通过有界 channel 收集；
- 每 100–500 ms 或 250–2000 行提交一次，以先到者为准；
- 参数可由基准测试调整；
- 一次事务使用 prepared statement；
- 会话结束强制 flush；
- 崩溃后允许最后一个未提交批次丢失，并在会话中记录。

### 12.4 测试结果

- 每次运行保存不可变测试定义快照。
- `test_runs` 保存完整不可变 `TestPlan` JSON 和计划哈希；启动时把开放运行封存为
  `Interrupted`，旧 schema 中没有计划正文的遗留运行只保留中断证据，不自动续作。
- 精确计划哈希、重新枚举证据和用户再次确认共同构成测试续作门槛。
- 标量指标进入 `test_metrics`。
- 延迟分布进入 `latency_histograms`，不得只保存 P99。
- 大型原始事件可使用分块压缩附件，数据库保存索引与哈希。
- 环境快照、应用版本、算法版本和缓存 flags 与结果绑定。

Dite V23/V24 旧结果使用独立的 `legacy_test_*` 表，避免伪装成由 V0.2 计划实际
执行的现代 `test_runs`。App 只发送源路径和已解析 SHA-256；Agent 重新只读解析并
核对哈希后，以来源 SHA-256 幂等写入运行和指标。数据库只保存源文件名，不保存源
绝对路径，不跟随旧 CSV 中的日志路径，也不读取日志引用的测试文件。

### 12.5 数据保留

- 默认不自动删除原始证据。
- 可生成 1 秒、1 分钟等 rollup，但不替代原始数据。
- 用户明确执行归档后，可把旧会话导出为带校验和的包。
- 测试阶段允许重建数据库或手工迁移，不承诺旧 schema 自动升级。

## 13. 原生采集替换与脚本保留

测试/监控重写不要求立即移除 PowerShell 采集。顺序为：

1. 先把旧脚本放到新 `IInventoryProvider` 之后。
2. 新原生提供程序逐类实现系统、磁盘、卷、池、层、虚拟磁盘和硬件。
3. SQLite 保存两种来源的字段级对照。
4. 每项算法和采集方式登记置信度。
5. 后续单独阶段确认全部通过后再移除脚本。

## 14. 工具适配器验证

- 每个支持版本保存一组脱敏的 DiskSpd XML、fio JSON、RoboCopy 日志和 RAMMap
  调用/退出结果样例。
- 参数映射测试比较生成的参数数组，不启动通用 shell。
- RAMMap 测试覆盖 `-Es`、`-Et` 白名单映射、拒绝额外参数、身份变化、缺失工具、
  用户确认、Broker 提权和前后快照证据。
- 解析器使用录制样例和错误/截断/多语言样例回放。
- 端到端测试在当前机器的专用测试目录执行，清理仅处理登记文件。
- 同时保存工具原始结果和 WinPool 归一化结果。
- 工具版本变化且未验证时显示“不受支持”或“实验性”，不得静默沿用旧解析器。
- 先验证普通单卷，再验证 Storage Spaces、性能层耗尽和接近满池场景。
- 任何端到端测试都不得初始化、格式化、调整分区或修改存储池结构。
