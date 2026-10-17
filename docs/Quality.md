# WinPool 验证与验收

2026-10-17：当前代码 V0.60，目标 V0.61。**本轮仅完成静态代码核对与文档整理，没有编译、测试、GUI、实机或性能运行。** 下一轮按[Plan](Plan.md)实施后才记录新结果。本文件只维护验证方法、当前短状态和证据入口。

## 最小充分验证

| 改动 | 验证范围 |
| --- | --- |
| 纯文档 | 当前内容／链接、归档完整性、Git 范围；不运行产品或测试 |
| 低影响文案、布局、设置呈现 | 通常一次编译；仅确需判断视觉时检查所改场景，不写镜像测试 |
| 规则、提交、异常流程 | 对应行为回归，覆盖会复发的成功／拒绝／冲突或未知分支 |
| 采集、真实执行、持久化 | 相关 fixture，加本轮改变动作的必要设备闭环；不全套重演历史 |
| V0.61 收口 | 稳定代码一次完整自动门、一个集中 GUI／设备批次、一次实际候选包启动 |

一个执行者跑验证，其他 Agent 审阅结果。已有通过检查未受后续改动影响就复用；失败只补相关路径。编译成功、模拟通过、单设备实测分别证明不同事情，不能互相替代。skip、未运行和缺环境均不算 passed。

不再以文档固定句子、文件数量、XAML 子串或无语义 wrapper 的存在作为产品验收。保留真实依赖方向、单写入方、准确目标、固定命令、事务、Unknown 不重放等行为检查。修 fixture 不能放松生产校验；删除旧实现时将有价值行为用例迁到实际所有者，其余同退役。

## 当前证据与缺口

| 证据 | 可以证明什么 |
| --- | --- |
| [2026-10-17 仓库审查 §§3、11–12](Review/WinPool-Repository-Audit-20261017.md) | 首轮 2418 passed／34 failed／3 skip；两个守卫在停止进程后通过，修正 admission 口径后的汇总 2420／32／3 不是重跑全套；30 个恢复 fixture 和2个文本断言须处理 |
| [同报告 §12](Review/WinPool-Repository-Audit-20261017.md) | Medium 默认 Storage 55.204 s／7条失败，现有无 discovery 参数探针 3.389 s／0失败；候选诊断，不是产品已修复，也不外推通用倍数 |
| [全量审查](Review/WinPool-Full-Review-20261017.md)、[接手审查](Review/WinPool-Takeover-Review-20261017.md) | 不同运行条件下的发现；后者额外超时、归档和轮转问题须按实际故障确认，不能把所有失败加成同一产品 bug 数 |
| [2026-10-16 VD耗时](Reference/Pool-VD-Timing-20261016.md) | 删除和 MAX 创建／自动布局中的重复采集成本；不是 V0.61 的性能结果 |
| [历史完整 Quality](Archive/20261017-v061-document-baseline/Quality-before.md) | V0.58–V0.60 各日期、动作、GUI、设备、工程门的原始结论与未测边界 |
| [单HDD容量调查](Reference/Single-Hdd-Tier-Capacity-20261009.md)、[整数GiB算法验收](Archive/20261012-editor-ui-baseline/Plan.md#104-用户指定的内置整数-gib-max-算法已验证) | 已有本机 WDC 的明确容量及普通／单HDD MAX 证据，不证明新重构或多成员硬件已验证 |

<a id="editor-320"></a>
<a id="v060-compact-actions"></a>
2026-10-16 表单、紧凑动作和自动盘符历史结果见[原记录](Archive/20261017-v061-document-baseline/Quality-before.md#editor-320)。随后共享项名改为100 DIP，只做相关编译／GUI；当前源码尺寸归 `EditorFormMetrics`，不将前一轮80 DIP结果当成当前值。

V0.61 当前状态：工作包代码、计时、集中验收和候选包均**未开始**；当前文档本地链接／章节、16份原件归档及Git范围已核对，源码、测试、构建脚本和版本源没有改动。15份tracked原件与基线Git内容一致，用户原提案与移动前SHA-256一致。跨 Windows、SSD／SCM、多成员、Mirror／Parity、多层 MAX 和显示环境的矩阵交首稿外部测试；现有开发机不足以证明全部覆盖。没有“上个公开测试包”的升级前置，使用现有 schema fixture 与开发数据副本验证本次改变的数据路径。

## 后续可用命令

以下命令仅供代码获批后执行，本轮没有运行。先停止占用标准运行树的已核实 App／Agent，后按需要 restore。定向用例由 Plan 各包指定现有测试类：

```powershell
dotnet restore WinPool.slnx
dotnet test tests/WinPool.Persistence.Tests/WinPool.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SimulationDocumentRepositoryTests" -m:1
```

稳定后执行一次全套并生成最终运行树：

```powershell
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
```

依赖有变化或已有结果不适用时再执行 `dotnet list WinPool.slnx package --vulnerable --include-transitive`，不作为每个工作包的前置。记录实际命令、基线和结果即可，不另建工程审计平台。

现有 `ManualPerformance` 三项门控测量保留显式 skip。当前不存在全仓 `Synthetic`／`HostReadOnly` 分类；不得写不存在的 filter 充当验收命令。`RealAdmissionReadOnlyTests` 的环境开关 return 应在 V0.61 改为显式 skip；合成恢复 fixture 注入能力结果，不能偷偷读取主机 WMI。测试诊断用临时目录或现有可注入 sink，不写用户 Diagnostics。

## 行为与性能口径

- 模拟：规则允许／拒绝／信息不足；合法预览与提交一致；CAS 原子性；重复 commitId 与丢回复对账；失败保留草稿。只增加此次重复路径的少量计数断言。
- 采集与真实：scope／代次／准确身份、缺失值、目标角色、真实写前能力、CallIssued 耐久、Unknown 不重放。正常单写三次预算的适用单位和例外严格按 Plan；MAX 按 attempt 记录，容量拒绝证明所需观察保留。
- UI：同 revision 复用预演，相同进度不重建拓扑；选择改变只更新选择呈现，基线改变仍触发真实冲突处理。
- IPC／监控：实际 wire 大小、批次序列去重、上游丢样可见、溢出／重连同步、终态耐久。不能靠降低20 Hz采样率掩盖传输成本。
- 性能：先证明重复调用／传输／计算消失，再比较同设备、同文档、同监控设置的墙钟。只计底层实际 invocation，嵌套计时不相加；用户准备、写调用、后态和界面完成分清。不预先承诺秒数或倍数。
- 归档路径确有改动时：持续样本按身份／值核对、恢复数据与根路径一致、失败保留有效原件、缓冲缺口如实报告；一段普通轮换不能冒充1 GiB长期压力验收。未改的专项不强制重做。

原生操作通过适用 WinUI 工具观察，浏览器 DOM 不替代原生桌面证据。真实修改沿用 [AGENTS](../AGENTS.md) 的 WDC 授权和末态，两块 Samsung 禁止写；自动测试不触碰真实存储结构。

最终只需要一份短结果：版本／基线、完成及退役的路径、实际验证与输出链接、未解决／未覆盖项、候选包与说明。放在本文件当前状态或 Plan 收口段，不再新建全面审查报告。
